using System.IO;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Archive;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public class RemediationVerificationTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestAppEnvironment _env;

    public RemediationVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_RemedTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _env = new TestAppEnvironment(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(500L, "500 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.00 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(5368709120L, "5.00 GB")]
    [InlineData(-1048576L, "-1.00 MB")]
    [InlineData(-1024L, "-1.0 KB")]
    public void FileSizeFormatter_FormatsCorrectly(long bytes, string expected)
    {
        var result = FileSizeFormatter.Format(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("-456", "-456")]
    [InlineData("+789", "+789")]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("+12.34", "+12.34")]
    public void CsvSanitizer_AllowsStandardNumbers(string input, string expected)
    {
        var escaped = CsvSanitizer.EscapeCsv(input);
        Assert.Equal(expected, escaped);
    }

    [Theory]
    [InlineData("+1e5")]
    [InlineData("-1e5")]
    [InlineData("=1e5")]
    [InlineData("@SUM(A1)")]
    [InlineData("=cmd|'/C calc'!A0")]
    [InlineData("-2+3*[1]")]
    public void CsvSanitizer_EscapesFormulaBypassesAndScientificNotations(string formula)
    {
        var escaped = CsvSanitizer.EscapeCsv(formula);
        Assert.StartsWith("'", escaped);
    }

    [Fact]
    public void AppVersion_ReturnsNonEmptyVersionSafely()
    {
        var version = AppVersion.Current;
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void FilesystemScanner_ImplementsIDisposable_DisposesCleanly()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var cfg = new JsonConfigurationService(_env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        var scanner = new FilesystemScanner(repo, cfg, NullLogger<FilesystemScanner>.Instance);

        Assert.IsAssignableFrom<IDisposable>(scanner);
        scanner.Dispose();
        scanner.Dispose(); // Multiple disposes must be safe
    }

    [Fact]
    public async Task ArchivePlannerService_SavePlanAsync_RejectsOverlappingPaths()
    {
        var service = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);

        var plan = new ArchivePlan
        {
            Batches = new List<ArchiveBatch>
            {
                new()
                {
                    BatchName = "InvalidBatch",
                    SourcePath = @"C:\Data\Folder",
                    PlannedDestinationPath = @"C:\Data\Folder\Subfolder"
                }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SavePlanAsync(plan));
    }

    [Fact]
    public void TreemapPartitionHelper_FiltersOverlappingDirectoriesCorrectly()
    {
        var dirs = new List<DirectorySummary>
        {
            new() { DirectoryPath = @"C:\Projects", RecursiveSizeBytes = 1000 },
            new() { DirectoryPath = @"C:\Projects\LocalFileAudit", RecursiveSizeBytes = 500 },
            new() { DirectoryPath = @"C:\Projects\LocalFileAudit\bin", RecursiveSizeBytes = 200 },
            new() { DirectoryPath = @"C:\Other", RecursiveSizeBytes = 300 }
        };

        var filtered = TreemapPartitionHelper.FilterNonOverlappingDirectories(dirs);

        // Leaves only: C:\Projects\LocalFileAudit\bin and C:\Other
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, d => d.DirectoryPath == @"C:\Projects\LocalFileAudit\bin");
        Assert.Contains(filtered, d => d.DirectoryPath == @"C:\Other");
        Assert.DoesNotContain(filtered, d => d.DirectoryPath == @"C:\Projects");
    }

    [Fact]
    public void FileRecord_SelfDerivesParentPathAndFileNameWhenEmpty()
    {
        var record = new FileRecord
        {
            FullPath = @"C:\Data\Logs\app.log"
        };

        Assert.Equal(@"C:\Data\Logs", record.ParentPath);
        Assert.Equal("app.log", record.FileName);

        // Explicit overrides must be preserved
        record.ParentPath = @"D:\Custom";
        record.FileName = "custom.txt";
        Assert.Equal(@"D:\Custom", record.ParentPath);
        Assert.Equal("custom.txt", record.FileName);
    }

    [Fact]
    public async Task SqliteScanRepository_IncrementalVacuumAndVacuumDatabase_ExecuteCleanly()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        // Should complete without throwing exceptions
        await repo.IncrementalVacuumAsync(100);
        await repo.VacuumDatabaseAsync();
    }

    [Fact]
    public async Task SqliteScanRepository_BatchedDeletionAndCheckpointing_ReclaimsCleanly()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var scanId = Guid.NewGuid();

        await repo.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            StartTimeUtc = DateTime.UtcNow
        });

        var files = new List<FileRecord>();
        for (int i = 0; i < 50; i++)
        {
            files.Add(new FileRecord
            {
                ScanRunId = scanId,
                FullPath = $@"C:\TestFolder\file_{i}.txt",
                SizeBytes = 1024,
                Extension = ".txt"
            });
        }

        await repo.InsertFileBatchAsync(files);
        var count = await repo.GetFileCountForScanAsync(scanId);
        Assert.Equal(50, count);

        // Discard should remove all records in bounded batches and checkpoint/vacuum
        await repo.DiscardScanRunAsync(scanId);
        var countAfter = await repo.GetFileCountForScanAsync(scanId);
        Assert.Equal(0, countAfter);
    }

    [Fact]
    public async Task SqliteScanRepository_DiscardScanRunAsync_ReportsAccurateProgress()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var scanId = Guid.NewGuid();

        await repo.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            StartTimeUtc = DateTime.UtcNow
        });

        var files = new List<FileRecord>();
        for (int i = 0; i < 20; i++)
        {
            files.Add(new FileRecord
            {
                ScanRunId = scanId,
                FullPath = $@"C:\ProgressTest\file_{i}.txt",
                SizeBytes = 2048,
                Extension = ".txt"
            });
        }
        await repo.InsertFileBatchAsync(files);

        var reportedProgress = new List<DatabaseProgress>();
        var syncReporter = new SynchronousProgress<DatabaseProgress>(p => reportedProgress.Add(p));

        await repo.DiscardScanRunAsync(scanId, default, syncReporter);

        Assert.NotEmpty(reportedProgress);
        Assert.Contains(reportedProgress, p => p.Percent >= 0.0);
        Assert.Contains(reportedProgress, p => p.Percent == 100.0);
        Assert.Equal(0, await repo.GetFileCountForScanAsync(scanId));
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class TestAppEnvironment : IAppEnvironment
    {
        public string ApplicationDataDirectory { get; }
        public string DatabaseDirectory => Path.Combine(ApplicationDataDirectory, "database");
        public string ReportsDirectory => Path.Combine(ApplicationDataDirectory, "reports");
        public string LogsDirectory => Path.Combine(ApplicationDataDirectory, "logs");
        public string BackupsDirectory => Path.Combine(ApplicationDataDirectory, "backups");
        public string ConfigFilePath => Path.Combine(ApplicationDataDirectory, "config.json");
        public string PlansDirectory => Path.Combine(ApplicationDataDirectory, "plans");
        public string PlansFilePath => Path.Combine(PlansDirectory, "archive-plan.json");

        public TestAppEnvironment(string root)
        {
            ApplicationDataDirectory = root;
        }

        public void EnsureDirectoriesCreated()
        {
            Directory.CreateDirectory(ApplicationDataDirectory);
            Directory.CreateDirectory(DatabaseDirectory);
            Directory.CreateDirectory(ReportsDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
            Directory.CreateDirectory(PlansDirectory);
        }
    }
}
