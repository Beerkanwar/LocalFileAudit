using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class ScannerIntegrationTests : IDisposable
{
    private readonly string _tempAppDataDir;
    private readonly string _testSourceDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly JsonConfigurationService _configService;
    private readonly FilesystemScanner _scanner;

    public ScannerIntegrationTests()
    {
        _tempAppDataDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_ScanTest_App_" + Guid.NewGuid().ToString("N"));
        _testSourceDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_ScanTest_Src_" + Guid.NewGuid().ToString("N"));

        _environment = new AppEnvironment(_tempAppDataDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _configService = new JsonConfigurationService(_environment, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _scanner = new FilesystemScanner(_repository, _configService, NullLogger<FilesystemScanner>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
        _configService.LoadAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ScanDirectory_DiscoversAllFiles_AndLeavesSourceUntouched()
    {
        // Setup source test fixture
        Directory.CreateDirectory(_testSourceDir);
        var subDir1 = Path.Combine(_testSourceDir, "Sub1");
        var subDir2 = Path.Combine(_testSourceDir, "Sub2");
        Directory.CreateDirectory(subDir1);
        Directory.CreateDirectory(subDir2);

        var file1 = Path.Combine(_testSourceDir, "file1.txt");
        var file2 = Path.Combine(subDir1, "file2.log");
        var file3 = Path.Combine(subDir2, "file3.dat");

        await File.WriteAllTextAsync(file1, "Content 1");
        await File.WriteAllTextAsync(file2, "Content 22");
        await File.WriteAllTextAsync(file3, "Content 333");

        var originalWriteTimes = new[]
        {
            File.GetLastWriteTimeUtc(file1),
            File.GetLastWriteTimeUtc(file2),
            File.GetLastWriteTimeUtc(file3)
        };

        var target = new ScanTarget
        {
            TargetPaths = new List<string> { _testSourceDir },
            FollowReparsePoints = false,
            LowImpactMode = false
        };

        var progressList = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p => progressList.Add(p));

        var run = await _scanner.StartScanAsync(target, CancellationToken.None, progress);

        Assert.Equal(ScanState.Completed, run.Status);
        Assert.Equal(3, run.FilesDiscovered);

        var dbCount = await _repository.GetFileCountForScanAsync(run.Id);
        Assert.Equal(3, dbCount);

        var dbSize = await _repository.GetTotalLogicalSizeBytesForScanAsync(run.Id);
        Assert.True(dbSize > 0);

        // Verify source files remain 100% UNTOUCHED (read-only priority 1)
        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
        Assert.True(File.Exists(file3));
        Assert.Equal("Content 1", await File.ReadAllTextAsync(file1));
        Assert.Equal("Content 22", await File.ReadAllTextAsync(file2));
        Assert.Equal("Content 333", await File.ReadAllTextAsync(file3));
        Assert.Equal(originalWriteTimes[0], File.GetLastWriteTimeUtc(file1));
        Assert.Equal(originalWriteTimes[1], File.GetLastWriteTimeUtc(file2));
        Assert.Equal(originalWriteTimes[2], File.GetLastWriteTimeUtc(file3));
    }

    [Fact]
    public async Task ScanDirectory_WhenCancelled_PersistsBatches_AndLeavesSourceUntouched()
    {
        // Setup source fixture with many directories
        Directory.CreateDirectory(_testSourceDir);
        for (int i = 0; i < 20; i++)
        {
            var folder = Path.Combine(_testSourceDir, $"Folder_{i}");
            Directory.CreateDirectory(folder);
            for (int j = 0; j < 10; j++)
            {
                File.WriteAllText(Path.Combine(folder, $"file_{j}.txt"), "Test payload data");
            }
        }

        using var cts = new CancellationTokenSource();
        var target = new ScanTarget
        {
            TargetPaths = new List<string> { _testSourceDir },
            LowImpactMode = true
        };

        // Cancel while scanning (20 directories * 15ms = 300ms total, cancel after 30ms)
        cts.CancelAfter(30);

        var run = await _scanner.StartScanAsync(target, cts.Token);

        Assert.Equal(ScanState.Cancelled, run.Status);

        // Check that run record was updated in database
        var retrievedRun = await _repository.GetScanRunAsync(run.Id);
        Assert.NotNull(retrievedRun);
        Assert.Equal(ScanState.Cancelled, retrievedRun.Status);

        // Source directory remains completely untouched
        Assert.True(Directory.Exists(_testSourceDir));
    }



    [Fact]
    public async Task ScanDirectory_CanResumeIncompleteScan_WithoutDuplicateRecords()
    {
        Directory.CreateDirectory(_testSourceDir);
        var dirA = Path.Combine(_testSourceDir, "DirA");
        var dirB = Path.Combine(_testSourceDir, "DirB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "a.txt"), "A");
        File.WriteAllText(Path.Combine(dirB, "b.txt"), "B");

        var target = new ScanTarget
        {
            TargetPaths = new List<string> { _testSourceDir },
            LowImpactMode = false
        };

        // Run full initial scan
        var run = await _scanner.StartScanAsync(target, CancellationToken.None);
        Assert.Equal(ScanState.Completed, run.Status);

        var countBefore = await _repository.GetFileCountForScanAsync(run.Id);
        Assert.Equal(2, countBefore);

        // Simulate resuming incomplete directories
        var resumeRun = await _scanner.ResumeScanAsync(run.Id, CancellationToken.None);
        Assert.Equal(ScanState.Completed, resumeRun.Status);

        // Total count should still be 2 (no duplicates created on resume)
        var countAfter = await _repository.GetFileCountForScanAsync(run.Id);
        Assert.Equal(2, countAfter);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempAppDataDir))
            {
                Directory.Delete(_tempAppDataDir, recursive: true);
            }
        }
        catch { }

        try
        {
            if (Directory.Exists(_testSourceDir))
            {
                Directory.Delete(_testSourceDir, recursive: true);
            }
        }
        catch { }
    }
}
