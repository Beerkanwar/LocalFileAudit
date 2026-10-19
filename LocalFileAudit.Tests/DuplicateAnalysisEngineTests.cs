using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class DuplicateAnalysisEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly DuplicateAnalysisEngine _engine;

    public DuplicateAnalysisEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_DupTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _environment = new AppEnvironment(_tempDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _engine = new DuplicateAnalysisEngine(_initializer, _repository, NullLogger<DuplicateAnalysisEngine>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public async Task FindPotentialDuplicatesBySizeAsync_OnlyReturnsGroupsWithTwoOrMoreFiles()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        var records = new List<FileRecord>
        {
            // Group 1: 10,000 bytes (2 files) -> Wasted = 10,000
            new() { ScanRunId = scanId, FullPath = @"C:\Dir1\FileA.dat", ParentPath = @"C:\Dir1", FileName = "FileA.dat", Extension = ".dat", SizeBytes = 10000, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = @"C:\Dir2\FileB.dat", ParentPath = @"C:\Dir2", FileName = "FileB.dat", Extension = ".dat", SizeBytes = 10000, ObservedAtUtc = DateTime.UtcNow },

            // Singleton: 5,000 bytes (1 file) -> Should be ignored
            new() { ScanRunId = scanId, FullPath = @"C:\Dir1\FileUnique.dat", ParentPath = @"C:\Dir1", FileName = "FileUnique.dat", Extension = ".dat", SizeBytes = 5000, ObservedAtUtc = DateTime.UtcNow },

            // Group 2: 2,000 bytes (3 files) -> Wasted = 4,000
            new() { ScanRunId = scanId, FullPath = @"C:\Dir1\FileC.dat", ParentPath = @"C:\Dir1", FileName = "FileC.dat", Extension = ".dat", SizeBytes = 2000, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = @"C:\Dir2\FileD.dat", ParentPath = @"C:\Dir2", FileName = "FileD.dat", Extension = ".dat", SizeBytes = 2000, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = @"C:\Dir3\FileE.dat", ParentPath = @"C:\Dir3", FileName = "FileE.dat", Extension = ".dat", SizeBytes = 2000, ObservedAtUtc = DateTime.UtcNow },

            // Below minimum size: 50 bytes (2 files)
            new() { ScanRunId = scanId, FullPath = @"C:\Dir1\Tiny1.txt", ParentPath = @"C:\Dir1", FileName = "Tiny1.txt", Extension = ".txt", SizeBytes = 50, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = @"C:\Dir2\Tiny2.txt", ParentPath = @"C:\Dir2", FileName = "Tiny2.txt", Extension = ".txt", SizeBytes = 50, ObservedAtUtc = DateTime.UtcNow }
        };

        await _repository.InsertFileBatchAsync(records);

        var summary = await _engine.FindPotentialDuplicatesBySizeAsync(scanId, minSizeBytes: 1000);

        Assert.Equal(2, summary.PotentialGroupsCount);
        Assert.Equal(5, summary.TotalDuplicateFilesCount);
        Assert.Equal(14000, summary.PotentialWastedSizeBytes);
        Assert.Equal(2, summary.Groups.Count);

        // First group should be the one with the highest wasted space: 10,000 bytes
        Assert.Equal(10000, summary.Groups[0].SizeBytes);
        Assert.Equal(2, summary.Groups[0].Files.Count);
        Assert.Equal(10000, summary.Groups[0].WastedSizeBytes);

        // Second group: 2,000 bytes
        Assert.Equal(2000, summary.Groups[1].SizeBytes);
        Assert.Equal(3, summary.Groups[1].Files.Count);
        Assert.Equal(4000, summary.Groups[1].WastedSizeBytes);
    }

    [Fact]
    public async Task ComputeHashesAndVerifyDuplicatesAsync_DifferentiatesMatchingSizeWithDifferentContent()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        // Create 3 actual files on disk
        var fileA = Path.Combine(_tempDir, "FileA.bin");
        var fileB = Path.Combine(_tempDir, "FileB.bin");
        var fileC = Path.Combine(_tempDir, "FileC.bin");

        // A and B have identical content
        var contentA = new byte[2048];
        Array.Fill(contentA, (byte)0xAA);
        await File.WriteAllBytesAsync(fileA, contentA);
        await File.WriteAllBytesAsync(fileB, contentA);

        // C has same size, but different content
        var contentC = new byte[2048];
        Array.Fill(contentC, (byte)0xBB);
        await File.WriteAllBytesAsync(fileC, contentC);

        var now = DateTime.UtcNow;
        var records = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = fileA, ParentPath = _tempDir, FileName = "FileA.bin", Extension = ".bin", SizeBytes = 2048, LastWriteTimeUtc = now, ObservedAtUtc = now },
            new() { ScanRunId = scanId, FullPath = fileB, ParentPath = _tempDir, FileName = "FileB.bin", Extension = ".bin", SizeBytes = 2048, LastWriteTimeUtc = now, ObservedAtUtc = now },
            new() { ScanRunId = scanId, FullPath = fileC, ParentPath = _tempDir, FileName = "FileC.bin", Extension = ".bin", SizeBytes = 2048, LastWriteTimeUtc = now, ObservedAtUtc = now }
        };

        await _repository.InsertFileBatchAsync(records);

        var progressUpdates = new List<HashJobProgress>();
        var progress = new Progress<HashJobProgress>(p => progressUpdates.Add(p));

        var summary = await _engine.ComputeHashesAndVerifyDuplicatesAsync(scanId, minSizeBytes: 1024, progress: progress);

        // Potential had 1 group with 3 files
        Assert.Equal(1, summary.PotentialGroupsCount);

        // Verified duplicates should only have 1 group with 2 files (FileA and FileB)
        Assert.Equal(1, summary.VerifiedGroupsCount);
        Assert.Equal(2, summary.TotalDuplicateFilesCount);
        Assert.Equal(2048, summary.VerifiedWastedSizeBytes);

        var group = summary.Groups.Single();
        Assert.Equal(2048, group.SizeBytes);
        Assert.NotNull(group.HashValue);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.FullPath == fileA);
        Assert.Contains(group.Files, f => f.FullPath == fileB);
        Assert.DoesNotContain(group.Files, f => f.FullPath == fileC);

        // Check that database was updated with hash values
        var queried = await _repository.QueryFilesAsync(new FileInventoryQuery { ScanRunId = scanId, Limit = 10 });
        var dbFileA = queried.Items.Single(f => f.FullPath == fileA);
        Assert.Equal("SHA-256", dbFileA.HashAlgorithm);
        Assert.Equal(group.HashValue, dbFileA.HashValue);
    }

    [Fact]
    public async Task ComputeHashesAndVerifyDuplicatesAsync_HandlesMissingFilesGracefully()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        // Reference paths that do not exist on disk
        var nonExistent1 = Path.Combine(_tempDir, "DoesNotExist1.bin");
        var nonExistent2 = Path.Combine(_tempDir, "DoesNotExist2.bin");

        var records = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = nonExistent1, ParentPath = _tempDir, FileName = "DoesNotExist1.bin", Extension = ".bin", SizeBytes = 4096, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = nonExistent2, ParentPath = _tempDir, FileName = "DoesNotExist2.bin", Extension = ".bin", SizeBytes = 4096, ObservedAtUtc = DateTime.UtcNow }
        };

        await _repository.InsertFileBatchAsync(records);

        var summary = await _engine.ComputeHashesAndVerifyDuplicatesAsync(scanId, minSizeBytes: 1024);

        // Size group was detected in Stage 1
        Assert.Equal(1, summary.PotentialGroupsCount);

        // But verified groups should be 0 since hashing failed safely on missing files
        Assert.Equal(0, summary.VerifiedGroupsCount);
        Assert.Empty(summary.Groups);
    }

    [Fact]
    public async Task ComputeHashesAndVerifyDuplicatesAsync_RespectsCancellation()
    {
        var scanId = Guid.NewGuid();
        await _repository.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed });

        var fileA = Path.Combine(_tempDir, "CancelA.bin");
        var fileB = Path.Combine(_tempDir, "CancelB.bin");
        await File.WriteAllBytesAsync(fileA, new byte[2048]);
        await File.WriteAllBytesAsync(fileB, new byte[2048]);

        var records = new List<FileRecord>
        {
            new() { ScanRunId = scanId, FullPath = fileA, ParentPath = _tempDir, FileName = "CancelA.bin", Extension = ".bin", SizeBytes = 2048, ObservedAtUtc = DateTime.UtcNow },
            new() { ScanRunId = scanId, FullPath = fileB, ParentPath = _tempDir, FileName = "CancelB.bin", Extension = ".bin", SizeBytes = 2048, ObservedAtUtc = DateTime.UtcNow }
        };
        await _repository.InsertFileBatchAsync(records);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _engine.ComputeHashesAndVerifyDuplicatesAsync(scanId, minSizeBytes: 1024, cancellationToken: cts.Token);
        });
    }
}
