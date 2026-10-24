using System.Collections.Concurrent;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Archive;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Logging;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public class PerformanceOptimizationTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppEnvironment _env;

    public PerformanceOptimizationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_PerfTests_" + Guid.NewGuid().ToString("N"));
        _env = new AppEnvironment(_testDir);
        _env.EnsureDirectoriesCreated();
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
        catch { }
    }

    [Fact]
    public async Task DatabaseInitializer_InitializesQuicklyAndCreatesScanMetricsTable()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        Assert.True(File.Exists(init.DatabasePath));

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var scanId = Guid.NewGuid();
        await repo.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        var initialMetrics = await repo.GetScanMetricsAsync(scanId);
        Assert.Null(initialMetrics);

        var summary = new StorageMetricsSummary
        {
            ScanRunId = scanId,
            TotalFiles = 10,
            TotalLogicalSizeBytes = 1048576,
            CloudPlaceholderSizeBytes = 0,
            CloudPlaceholderCount = 0,
            VolumeFreeBytes = 50000000000,
            VolumeCapacityBytes = 100000000000,
            TopExtensions = new List<ExtensionSummary>
            {
                new ExtensionSummary { Extension = ".log", TotalLogicalSizeBytes = 1048576, FileCount = 10 }
            },
            LargestDirectories = new List<DirectorySummary>
            {
                new DirectorySummary { DirectoryPath = @"C:\Test", RecursiveSizeBytes = 1048576, RecursiveFileCount = 10 }
            },
            LargestFiles = new List<FileRecord>(),
            ReviewCandidates = new List<CandidateClassification>()
        };

        await repo.SaveScanMetricsAsync(scanId, summary);

        var retrieved = await repo.GetScanMetricsAsync(scanId);
        Assert.NotNull(retrieved);
        Assert.Equal(scanId, retrieved.ScanRunId);
        Assert.Equal(1048576, retrieved.TotalLogicalSizeBytes);
        Assert.Single(retrieved.TopExtensions);
        Assert.Equal(".log", retrieved.TopExtensions[0].Extension);
    }

    [Fact]
    public async Task UpdateDirectoryStatusBatchAsync_UpdatesMultipleDirectoriesInSingleTransaction()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        var scanId = Guid.NewGuid();
        var scanRun = new ScanRun { Id = scanId, Status = ScanState.InProgress };
        await repo.CreateScanRunAsync(scanRun);

        var dirs = new List<DirectoryRecord>
        {
            new DirectoryRecord { ScanRunId = scanId, Path = @"C:\Dir1", Status = DirectoryState.Pending },
            new DirectoryRecord { ScanRunId = scanId, Path = @"C:\Dir2", Status = DirectoryState.Pending },
            new DirectoryRecord { ScanRunId = scanId, Path = @"C:\Dir3", Status = DirectoryState.Pending }
        };
        await repo.InsertDirectoryBatchAsync(dirs);

        var inserted = await repo.GetPendingOrInterruptedDirectoriesAsync(scanId);
        Assert.Equal(3, inserted.Count);

        var updates = new List<DirectoryStatusUpdate>
        {
            new DirectoryStatusUpdate(inserted[0].Id, DirectoryState.Completed, 15, null),
            new DirectoryStatusUpdate(inserted[1].Id, DirectoryState.Completed, 25, null),
            new DirectoryStatusUpdate(inserted[2].Id, DirectoryState.Error, 0, "Access Denied")
        };

        await repo.UpdateDirectoryStatusBatchAsync(updates);

        var pending = await repo.GetPendingOrInterruptedDirectoriesAsync(scanId);
        Assert.Empty(pending);

        var completedCount = await repo.GetCompletedDirectoryCountForScanAsync(scanId);
        Assert.Equal(2, completedCount);
    }

    [Fact]
    public async Task DiscardScanRunAsync_BatchesMassDeletionsAndCleansUpScan()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        var scanId = Guid.NewGuid();
        var scanRun = new ScanRun { Id = scanId, Status = ScanState.Completed };
        await repo.CreateScanRunAsync(scanRun);

        var files = new List<FileRecord>();
        for (int i = 0; i < 50; i++)
        {
            files.Add(new FileRecord
            {
                ScanRunId = scanId,
                FullPath = $@"C:\Folder\file_{i}.dat",
                ParentPath = @"C:\Folder",
                FileName = $"file_{i}.dat",
                Extension = ".dat",
                SizeBytes = 100 * i,
                Attributes = 32,
                ObservedAtUtc = DateTime.UtcNow
            });
        }
        await repo.InsertFileBatchAsync(files);

        var preCount = await repo.GetFileCountForScanAsync(scanId);
        Assert.Equal(50, preCount);

        await repo.DiscardScanRunAsync(scanId);

        var postCount = await repo.GetFileCountForScanAsync(scanId);
        Assert.Equal(0, postCount);

        var deletedRun = await repo.GetScanRunAsync(scanId);
        Assert.Null(deletedRun);
    }

    [Fact]
    public async Task InvalidateCacheAsync_ClearsAnalysisEngineCacheAndDbResults()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var configService = new JsonConfigurationService(_env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        await configService.LoadAsync();

        var scanId = Guid.NewGuid();
        var scanRun = new ScanRun { Id = scanId, Status = ScanState.Completed };
        await repo.CreateScanRunAsync(scanRun);

        var candidates = new List<CandidateClassification>
        {
            new CandidateClassification
            {
                FileRecordId = 1,
                ScanRunId = scanId,
                Category = CandidateCategory.CleanupCandidate,
                RuleId = "TEMP-01",
                Priority = 1,
                Confidence = 1.0,
                Explanation = "Temp file",
                FullPath = @"C:\temp.tmp",
                FileName = "temp.tmp",
                Extension = ".tmp",
                SizeBytes = 500
            }
        };
        await repo.SaveAnalysisResultsAsync(scanId, candidates);

        var engine = new StorageAnalysisEngine(init, repo, configService, NullLogger<StorageAnalysisEngine>.Instance);
        await engine.InvalidateCacheAsync(scanId);

        var results = await repo.GetCachedAnalysisResultsAsync(scanId);
        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckpointTruncateAsync_CompletesSuccessfully()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        await repo.CheckpointTruncateAsync();
    }

    [Fact]
    public async Task FileLoggerProvider_AsynchronousChannelLogsEntriesToDisk()
    {
        var options = new FileLoggerOptions
        {
            LogDirectory = _env.LogsDirectory,
            MinLevel = LogLevel.Information
        };

        using var provider = new FileLoggerProvider(options);

        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Testing async channel logger message {Index}", 12345);

        // Give the background consumer channel time to write and flush
        await Task.Delay(250);

        var logFiles = Directory.GetFiles(_env.LogsDirectory, "*.log");
        Assert.NotEmpty(logFiles);

        var logContent = await File.ReadAllTextAsync(logFiles[0]);
        Assert.Contains("Testing async channel logger message 12345", logContent);
    }

    [Fact]
    public async Task ArchiveVerificationService_LazyStreamingVerificationCalculatesHashesCorrectly()
    {
        var sourceDir = Path.Combine(_testDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "document.txt");
        await File.WriteAllTextAsync(sourceFile, "This is verified content for testing hash streaming.");

        var destDir = Path.Combine(_testDir, "dest");
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, "document.txt");
        await File.WriteAllTextAsync(destFile, "This is verified content for testing hash streaming.");

        var verifier = new ArchiveVerificationService(_env, NullLogger<ArchiveVerificationService>.Instance);
        var result = await verifier.VerifyArchiveAsync(
            sourceDir,
            destDir,
            calculateSha256: true,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsPartial);
        Assert.Equal(1, result.TotalSourceFiles);
        Assert.Equal(1, result.MatchCount);
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(0, result.HashMismatchCount);
    }
}
