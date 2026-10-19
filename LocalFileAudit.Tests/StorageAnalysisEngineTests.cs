using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class StorageAnalysisEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly JsonConfigurationService _configService;
    private readonly StorageAnalysisEngine _engine;

    public StorageAnalysisEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_AnalysisTest_" + Guid.NewGuid().ToString("N"));
        _environment = new AppEnvironment(_tempDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _configService = new JsonConfigurationService(_environment, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _engine = new StorageAnalysisEngine(_initializer, _repository, _configService, NullLogger<StorageAnalysisEngine>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
        _configService.LoadAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task GetExtensionStatistics_GroupsAndSortsCorrectly()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\a.zip", ParentPath = @"D:\", FileName = "a.zip", Extension = ".zip", SizeBytes = 300_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\b.zip", ParentPath = @"D:\", FileName = "b.zip", Extension = ".zip", SizeBytes = 200_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\c.txt", ParentPath = @"D:\", FileName = "c.txt", Extension = ".txt", SizeBytes = 50_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\d.mp4", ParentPath = @"D:\", FileName = "d.mp4", Extension = ".mp4", SizeBytes = 400_000_000 }
        };

        await _repository.InsertFileBatchAsync(files);

        var stats = await _engine.GetExtensionStatisticsAsync(scanId);

        Assert.Equal(3, stats.Count);

        // .zip should be top by size (300MB + 200MB = 500MB > 400MB .mp4 > 50MB .txt)
        Assert.Equal(".zip", stats[0].Extension);
        Assert.Equal(2, stats[0].FileCount);
        Assert.Equal(500_000_000, stats[0].TotalLogicalSizeBytes);

        Assert.Equal(".mp4", stats[1].Extension);
        Assert.Equal(1, stats[1].FileCount);

        Assert.Equal(".txt", stats[2].Extension);
        Assert.Equal(1, stats[2].FileCount);

        // Percentage calculations
        Assert.True(stats[0].PercentageOfTotalSize > 50.0);
    }

    [Fact]
    public async Task GetLargestFiles_ReturnsTopFilesOrderedBySize()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\small.txt", ParentPath = @"D:\", FileName = "small.txt", Extension = ".txt", SizeBytes = 100 },
            new() { ScanRunId = scanId, FullPath = @"D:\huge.iso", ParentPath = @"D:\", FileName = "huge.iso", Extension = ".iso", SizeBytes = 5_000_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\medium.mp4", ParentPath = @"D:\", FileName = "medium.mp4", Extension = ".mp4", SizeBytes = 500_000_000 }
        };

        await _repository.InsertFileBatchAsync(files);

        var largest = await _engine.GetLargestFilesAsync(scanId, top: 2);

        Assert.Equal(2, largest.Count);
        Assert.Equal("huge.iso", largest[0].FileName);
        Assert.Equal("medium.mp4", largest[1].FileName);
    }

    [Fact]
    public async Task AnalyzeScan_BuildsComprehensiveSummary()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            SelectedDrives = "D:\\"
        });

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\Data\file1.dat", ParentPath = @"D:\Data", FileName = "file1.dat", Extension = ".dat", SizeBytes = 1000 },
            new() { ScanRunId = scanId, FullPath = @"D:\Data\file2.dat", ParentPath = @"D:\Data", FileName = "file2.dat", Extension = ".dat", SizeBytes = 2000 }
        };
        await _repository.InsertFileBatchAsync(files);

        var summary = await _engine.AnalyzeScanAsync(scanId);

        Assert.NotNull(summary);
        Assert.Equal(scanId, summary.ScanRunId);
        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(3000, summary.TotalLogicalSizeBytes);
        Assert.True(summary.IsScanComplete);
        Assert.Single(summary.TopExtensions);
        Assert.Equal(2, summary.LargestFiles.Count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
