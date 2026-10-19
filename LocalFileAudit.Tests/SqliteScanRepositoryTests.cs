using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class SqliteScanRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;

    public SqliteScanRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_RepoTest_" + Guid.NewGuid().ToString("N"));
        _environment = new AppEnvironment(_tempDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CreateAndRetrieveScanRun_WorksCorrectly()
    {
        var run = new ScanRun
        {
            Id = Guid.NewGuid(),
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.InProgress,
            SelectedDrives = "C:\\;D:\\",
            ConfigurationSnapshot = "{}",
            ApplicationVersion = "1.0.0"
        };

        await _repository.CreateScanRunAsync(run);

        var retrieved = await _repository.GetScanRunAsync(run.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(run.Id, retrieved.Id);
        Assert.Equal(ScanState.InProgress, retrieved.Status);
        Assert.Equal("C:\\;D:\\", retrieved.SelectedDrives);
    }

    [Fact]
    public async Task InsertFileBatch_RejectsDuplicatesSilentlyViaUniqueConstraint()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.InProgress };
        await _repository.CreateScanRunAsync(run);

        var file = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"C:\Test\Sample.txt",
            ParentPath = @"C:\Test",
            FileName = "Sample.txt",
            Extension = ".txt",
            SizeBytes = 1024,
            Attributes = 32,
            ObservedAtUtc = DateTime.UtcNow
        };

        // Insert first time
        await _repository.InsertFileBatchAsync(new[] { file });

        // Insert duplicate (same scanId and full path)
        await _repository.InsertFileBatchAsync(new[] { file });

        var count = await _repository.GetFileCountForScanAsync(scanId);
        Assert.Equal(1, count); // Duplicate was ignored, no exception thrown
    }

    [Fact]
    public async Task DiscardScanRun_RemovesOnlyDatabaseRecords_NeverTouchesFilesystem()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.InProgress };
        await _repository.CreateScanRunAsync(run);

        var file = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"C:\Fake\File.txt",
            ParentPath = @"C:\Fake",
            FileName = "File.txt",
            Extension = ".txt",
            SizeBytes = 500,
            Attributes = 32,
            ObservedAtUtc = DateTime.UtcNow
        };
        await _repository.InsertFileBatchAsync(new[] { file });

        var error = new ScanErrorRecord
        {
            ScanRunId = scanId,
            Path = @"C:\Fake\Locked",
            Operation = "Enumerate",
            ErrorMessage = "Access denied"
        };
        await _repository.InsertScanErrorAsync(error);

        // Discard
        await _repository.DiscardScanRunAsync(scanId);

        var retrievedRun = await _repository.GetScanRunAsync(scanId);
        Assert.Null(retrievedRun);

        var count = await _repository.GetFileCountForScanAsync(scanId);
        Assert.Equal(0, count);

        var errors = await _repository.GetScanErrorsAsync(scanId);
        Assert.Empty(errors);
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
            // Best effort
        }
    }
}
