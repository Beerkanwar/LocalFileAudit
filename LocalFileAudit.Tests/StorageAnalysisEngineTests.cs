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

    [Fact]
    public async Task AnalyzeScan_UnresolvableDrives_LeavesVolumeSpaceAsZero()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            SelectedDrives = "" // empty or unresolvable
        });

        var summary = await _engine.AnalyzeScanAsync(scanId);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.VolumeCapacityBytes);
        Assert.Equal(0, summary.VolumeFreeBytes);
    }

    [Fact]
    public async Task ClassifyCandidates_CachesResultsInAnalysisResultsTable()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            SelectedDrives = @"D:\"
        });

        // Add a temporary file candidate: extension .tmp, age > 180 days, large size
        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\temp\cache.tmp", ParentPath = @"D:\temp", FileName = "cache.tmp", Extension = ".tmp", SizeBytes = 1_000_000_000, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-200) }
        };
        await _repository.InsertFileBatchAsync(files);

        // First run computes and caches to AnalysisResults
        var candidates1 = await _engine.ClassifyCandidatesAsync(scanId);
        Assert.Single(candidates1);
        Assert.Equal("cache.tmp", candidates1[0].FileName);

        // Second run reads from SQLite AnalysisResults cache
        var candidates2 = await _engine.ClassifyCandidatesAsync(scanId);
        Assert.Single(candidates2);
        Assert.Equal(candidates1[0].FullPath, candidates2[0].FullPath);
        Assert.Equal(candidates1[0].Category, candidates2[0].Category);

        // InvalidateCache clears the memory cache
        _engine.InvalidateCache(scanId);

        // Third run still reads persisted AnalysisResults from DB
        var candidates3 = await _engine.ClassifyCandidatesAsync(scanId);
        Assert.Single(candidates3);
        Assert.Equal(candidates1[0].FullPath, candidates3[0].FullPath);
        Assert.Equal(candidates1[0].Category, candidates3[0].Category);
    }

    [Fact]
    public async Task GetLargestDirectories_WithNestedOnlyAndSiblingTrees_CalculatesRecursiveRollUpAccurately()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            SelectedDrives = @"D:\Root"
        });

        // Structure:
        // D:\Root\ParentWithNoDirectFiles\ChildA\file1 (100 MB)
        // D:\Root\ParentWithNoDirectFiles\ChildB\file2 (200 MB)
        // D:\Root\SiblingFolder\file3 (50 MB)
        var directories = new List<DirectoryRecord>
        {
            new() { ScanRunId = scanId, Path = @"D:\Root\ParentWithNoDirectFiles" },
            new() { ScanRunId = scanId, Path = @"D:\Root\ParentWithNoDirectFiles\ChildA" },
            new() { ScanRunId = scanId, Path = @"D:\Root\ParentWithNoDirectFiles\ChildB" },
            new() { ScanRunId = scanId, Path = @"D:\Root\SiblingFolder" }
        };
        await _repository.InsertDirectoryBatchAsync(directories);

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\Root\ParentWithNoDirectFiles\ChildA\file1.dat", ParentPath = @"D:\Root\ParentWithNoDirectFiles\ChildA", FileName = "file1.dat", Extension = ".dat", SizeBytes = 100_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\Root\ParentWithNoDirectFiles\ChildB\file2.dat", ParentPath = @"D:\Root\ParentWithNoDirectFiles\ChildB", FileName = "file2.dat", Extension = ".dat", SizeBytes = 200_000_000 },
            new() { ScanRunId = scanId, FullPath = @"D:\Root\SiblingFolder\file3.dat", ParentPath = @"D:\Root\SiblingFolder", FileName = "file3.dat", Extension = ".dat", SizeBytes = 50_000_000 }
        };
        await _repository.InsertFileBatchAsync(files);

        var dirs = await _engine.GetLargestDirectoriesAsync(scanId, top: 10);

        // ParentWithNoDirectFiles must appear as the largest (100MB + 200MB = 300MB recursive)
        // even though it contains 0 direct files!
        var parentSummary = dirs.FirstOrDefault(d => d.DirectoryPath.Equals(@"D:\Root\ParentWithNoDirectFiles", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(parentSummary);
        Assert.Equal(0, parentSummary.DirectFileCount);
        Assert.Equal(0, parentSummary.DirectSizeBytes);
        Assert.Equal(2, parentSummary.RecursiveFileCount);
        Assert.Equal(300_000_000, parentSummary.RecursiveSizeBytes);
        Assert.True(parentSummary.IsComplete);

        // ChildB has 200MB direct & recursive
        var childBSummary = dirs.FirstOrDefault(d => d.DirectoryPath.Equals(@"D:\Root\ParentWithNoDirectFiles\ChildB", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(childBSummary);
        Assert.Equal(1, childBSummary.DirectFileCount);
        Assert.Equal(200_000_000, childBSummary.DirectSizeBytes);
        Assert.Equal(1, childBSummary.RecursiveFileCount);
        Assert.Equal(200_000_000, childBSummary.RecursiveSizeBytes);

        // SiblingFolder has 50MB
        var siblingSummary = dirs.FirstOrDefault(d => d.DirectoryPath.Equals(@"D:\Root\SiblingFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(siblingSummary);
        Assert.Equal(1, siblingSummary.DirectFileCount);
        Assert.Equal(50_000_000, siblingSummary.DirectSizeBytes);
        Assert.Equal(1, siblingSummary.RecursiveFileCount);
        Assert.Equal(50_000_000, siblingSummary.RecursiveSizeBytes);
    }

    [Fact]
    public async Task GetLargestDirectories_WhenScanInProgress_SetsIsCompleteFalse()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.InProgress,
            SelectedDrives = @"D:\"
        });

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\Data\file1.dat", ParentPath = @"D:\Data", FileName = "file1.dat", Extension = ".dat", SizeBytes = 1000 }
        };
        await _repository.InsertFileBatchAsync(files);

        var dirs = await _engine.GetLargestDirectoriesAsync(scanId, top: 1);
        Assert.Single(dirs);
        Assert.False(dirs[0].IsComplete);
    }

    [Fact]
    public async Task AnalyzeScan_DuringInProgressScan_DoesNotPoisonCompletedCache()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.InProgress,
            SelectedDrives = @"D:\"
        });

        // 1. Initial batch of 2 files
        var batch1 = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\a.txt", ParentPath = @"D:\", FileName = "a.txt", Extension = ".txt", SizeBytes = 100 },
            new() { ScanRunId = scanId, FullPath = @"D:\b.txt", ParentPath = @"D:\", FileName = "b.txt", Extension = ".txt", SizeBytes = 200 }
        };
        await _repository.InsertFileBatchAsync(batch1);

        // 2. Analyze while in-progress
        var partialSummary = await _engine.AnalyzeScanAsync(scanId);
        Assert.False(partialSummary.IsScanComplete);
        Assert.Equal(2, partialSummary.TotalFiles);
        Assert.Equal(300, partialSummary.TotalLogicalSizeBytes);

        // 3. Scan completes and commits additional files
        var batch2 = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\c.txt", ParentPath = @"D:\", FileName = "c.txt", Extension = ".txt", SizeBytes = 500 }
        };
        await _repository.InsertFileBatchAsync(batch2);
        await _repository.UpdateScanRunStatusAsync(scanId, ScanState.Completed, DateTime.UtcNow, 3, 1, 0);

        // 4. Re-analyze: must NOT serve the partial 2-file cache
        var completedSummary = await _engine.AnalyzeScanAsync(scanId);
        Assert.True(completedSummary.IsScanComplete);
        Assert.Equal(3, completedSummary.TotalFiles);
        Assert.Equal(800, completedSummary.TotalLogicalSizeBytes);

        // 5. Subsequent call serves the cached completed summary
        var cachedSummary = await _engine.AnalyzeScanAsync(scanId);
        Assert.Same(completedSummary, cachedSummary);
    }

    [Fact]
    public async Task GetLargestDirectories_StopsAtScanRoots_AndDoesNotCreateAboveRootAncestors()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.Completed,
            SelectedDrives = @"D:\Root"
        });

        var directories = new List<DirectoryRecord>
        {
            new() { ScanRunId = scanId, Path = @"D:\Root" },
            new() { ScanRunId = scanId, Path = @"D:\Root\Child" }
        };
        await _repository.InsertDirectoryBatchAsync(directories);

        var files = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = @"D:\Root\Child\file.dat", ParentPath = @"D:\Root\Child", FileName = "file.dat", Extension = ".dat", SizeBytes = 1000 }
        };
        await _repository.InsertFileBatchAsync(files);

        var dirs = await _engine.GetLargestDirectoriesAsync(scanId, top: 10);

        // Must include D:\Root and D:\Root\Child, but must NEVER include D:\
        Assert.DoesNotContain(dirs, d => d.DirectoryPath.Equals(@"D:\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dirs, d => d.DirectoryPath.Equals(@"D:\Root", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dirs, d => d.DirectoryPath.Equals(@"D:\Root\Child", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClassifyCandidates_DoesNotPersistOrServeStaleCacheForInProgressScan()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun
        {
            Id = scanId,
            Status = ScanState.InProgress,
            SelectedDrives = @"D:\"
        });

        // 1. In-progress scan with 1 old .mp4 candidate
        var file1 = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"D:\old_movie.mp4",
            ParentPath = @"D:\",
            FileName = "old_movie.mp4",
            Extension = ".mp4",
            SizeBytes = 500_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddYears(-5)
        };
        await _repository.InsertFileBatchAsync(new[] { file1 });

        // 2. Classify candidates during in-progress scan
        var inProgressCandidates = await _engine.ClassifyCandidatesAsync(scanId);
        Assert.Single(inProgressCandidates);
        Assert.Equal("old_movie.mp4", inProgressCandidates[0].FileName);

        // AnalysisResults table in DB should be empty because scan is not complete!
        var dbCached = await _repository.GetCachedAnalysisResultsAsync(scanId);
        Assert.Empty(dbCached);

        // 3. Add second candidate and complete the scan
        var file2 = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"D:\second_movie.mp4",
            ParentPath = @"D:\",
            FileName = "second_movie.mp4",
            Extension = ".mp4",
            SizeBytes = 600_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddYears(-4)
        };
        await _repository.InsertFileBatchAsync(new[] { file2 });
        await _repository.UpdateScanRunStatusAsync(scanId, ScanState.Completed, DateTime.UtcNow, 2, 1, 0);

        // 4. Re-analyze: must return 2 candidates, not 1!
        var completedCandidates = await _engine.ClassifyCandidatesAsync(scanId);
        Assert.Equal(2, completedCandidates.Count);

        // DB should now cache both candidates
        var dbCachedAfterCompletion = await _repository.GetCachedAnalysisResultsAsync(scanId);
        Assert.Equal(2, dbCachedAfterCompletion.Count);

        // 5. InvalidateCache clears the cache
        _engine.InvalidateCache(scanId);
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
