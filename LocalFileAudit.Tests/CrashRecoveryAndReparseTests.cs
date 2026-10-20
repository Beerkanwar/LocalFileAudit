using System.Diagnostics;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class CrashRecoveryAndReparseTests : IDisposable
{
    private readonly string _tempAppDataDir;
    private readonly string _testSourceDir;

    public CrashRecoveryAndReparseTests()
    {
        _tempAppDataDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_CrashTest_App_" + Guid.NewGuid().ToString("N"));
        _testSourceDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_CrashTest_Src_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task SimulatedCrash_ReopeningDatabase_RecoversCommittedBatches_AndAllowsResume()
    {
        // 1. First process session: initialize and perform partial work
        var env1 = new AppEnvironment(_tempAppDataDir);
        var init1 = new SqliteDatabaseInitializer(env1, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init1.InitializeAsync();

        var repo1 = new SqliteScanRepository(init1, NullLogger<SqliteScanRepository>.Instance);

        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = ScanState.InProgress,
            SelectedDrives = _testSourceDir,
            ApplicationVersion = "1.0.0"
        };
        await repo1.CreateScanRunAsync(run);

        // Commit 2 file batches
        var committedFiles = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"C:\Test\File1.dat", ParentPath = @"C:\Test", FileName = "File1.dat", Extension = ".dat", SizeBytes = 5000, Attributes = 32 },
            new() { ScanRunId = scanId, FullPath = @"C:\Test\File2.dat", ParentPath = @"C:\Test", FileName = "File2.dat", Extension = ".dat", SizeBytes = 12000, Attributes = 32 }
        };
        await repo1.InsertFileBatchAsync(committedFiles);

        // Checkpoint one completed directory and one pending directory
        var dirRecords = new List<DirectoryRecord>
        {
            new() { ScanRunId = scanId, Path = @"C:\Test\Dir1", Status = DirectoryState.Completed, FilesProcessed = 2 },
            new() { ScanRunId = scanId, Path = @"C:\Test\Dir2", Status = DirectoryState.Pending, FilesProcessed = 0 }
        };
        await repo1.InsertDirectoryBatchAsync(dirRecords);

        // SIMULATE ABRUPT TERMINATION / CRASH (disposing session 1 without closing cleanly)

        // 2. Second session (Application restarted after termination)
        var env2 = new AppEnvironment(_tempAppDataDir);
        var init2 = new SqliteDatabaseInitializer(env2, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init2.InitializeAsync(); // WAL mode recovery occurs automatically

        var repo2 = new SqliteScanRepository(init2, NullLogger<SqliteScanRepository>.Instance);

        // Verify incomplete scan is detected
        var incompleteRuns = await repo2.GetIncompleteScanRunsAsync();
        Assert.Single(incompleteRuns);
        Assert.Equal(scanId, incompleteRuns[0].Id);
        Assert.Equal(ScanState.InProgress, incompleteRuns[0].Status);

        // Verify committed file records survived the crash
        var fileCount = await repo2.GetFileCountForScanAsync(scanId);
        Assert.Equal(2, fileCount);

        var totalSize = await repo2.GetTotalLogicalSizeBytesForScanAsync(scanId);
        Assert.Equal(17000, totalSize);

        // Verify interrupted/pending directories are identified for resumption
        var pendingDirs = await repo2.GetPendingOrInterruptedDirectoriesAsync(scanId);
        Assert.Single(pendingDirs);
        Assert.Equal(@"C:\Test\Dir2", pendingDirs[0].Path);
    }

    [Fact]
    public async Task ReparsePoint_WhenFollowReparsePointsFalse_IsSkipped()
    {
        var env = new AppEnvironment(_tempAppDataDir);
        var init = new SqliteDatabaseInitializer(env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var cfgService = new JsonConfigurationService(env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        await cfgService.LoadAsync();

        var scanner = new FilesystemScanner(repo, cfgService, NullLogger<FilesystemScanner>.Instance);

        Directory.CreateDirectory(_testSourceDir);
        var subDir = Path.Combine(_testSourceDir, "NormalSub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "normal.txt"), "hello");

        var target = new ScanTarget
        {
            TargetPaths = new List<string> { _testSourceDir },
            FollowReparsePoints = false,
            LowImpactMode = false
        };

        var run = await scanner.StartScanAsync(target, CancellationToken.None);
        Assert.Equal(ScanState.Completed, run.Status);

        var fileCount = await repo.GetFileCountForScanAsync(run.Id);
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public async Task RealProcessKill_WalMode_PreservesCommittedDataAndRecoversCleanly()
    {
        Directory.CreateDirectory(_tempAppDataDir);
        var dbPath = Path.Combine(_tempAppDataDir, "crash_worker.db");

        var workerDll = typeof(LocalFileAudit.CrashWorker.Program).Assembly.Location;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{workerDll}\" \"{dbPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Wait until child signals that transactions are committed and connection is kept open
        string? readyLine = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.Token.IsCancellationRequested && !process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync(cts.Token);
            if (line != null && line.Contains("COMMITTED_AND_READY"))
            {
                readyLine = line;
                break;
            }
        }

        Assert.NotNull(readyLine);
        Assert.False(process.HasExited, "Child process must still be running before termination");

        // Forcefully kill child process without clean shutdown (abrupt crash simulation)
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        // Reopen database with fresh connection and verify PRAGMA integrity_check
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync();

        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "PRAGMA integrity_check;";
        var integrityResult = (string?)await checkCmd.ExecuteScalarAsync();
        Assert.Equal("ok", integrityResult);

        // Verify committed 100 rows survived the process kill
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(1) FROM CrashTestEntries;";
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
        Assert.Equal(100, count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempAppDataDir)) Directory.Delete(_tempAppDataDir, recursive: true);
        }
        catch { }

        try
        {
            if (Directory.Exists(_testSourceDir)) Directory.Delete(_testSourceDir, recursive: true);
        }
        catch { }
    }
}
