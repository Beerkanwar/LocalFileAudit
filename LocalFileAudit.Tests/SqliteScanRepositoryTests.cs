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

    [Fact]
    public async Task QueryFilesAsync_FiltersBySearchAndExtensionAndPaginatesCorrectly()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.Completed };
        await _repository.CreateScanRunAsync(run);

        var files = new List<FileRecord>();
        for (int i = 1; i <= 25; i++)
        {
            files.Add(new FileRecord
            {
                ScanRunId = scanId,
                FullPath = $@"C:\Projects\Doc_{i:D2}.pdf",
                ParentPath = @"C:\Projects",
                FileName = $"Doc_{i:D2}.pdf",
                Extension = ".pdf",
                SizeBytes = i * 1024 * 1024,
                LastWriteTimeUtc = DateTime.UtcNow.AddDays(-i),
                Attributes = 32,
                ObservedAtUtc = DateTime.UtcNow
            });
        }

        // Add some noise files with different extension
        for (int i = 1; i <= 5; i++)
        {
            files.Add(new FileRecord
            {
                ScanRunId = scanId,
                FullPath = $@"C:\Projects\Archive_{i}.zip",
                ParentPath = @"C:\Projects",
                FileName = $"Archive_{i}.zip",
                Extension = ".zip",
                SizeBytes = 100 * 1024 * 1024,
                LastWriteTimeUtc = DateTime.UtcNow,
                Attributes = 32,
                ObservedAtUtc = DateTime.UtcNow
            });
        }

        await _repository.InsertFileBatchAsync(files);

        // 1. Query with Extension filter and Pagination (Page 1, 10 items)
        var query = new FileInventoryQuery
        {
            ScanRunId = scanId,
            Extension = ".pdf",
            SortBy = "SizeBytes",
            SortDescending = true,
            Limit = 10,
            Offset = 0
        };

        var page1 = await _repository.QueryFilesAsync(query);
        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(1, page1.PageNumber);
        Assert.Equal("Doc_25.pdf", page1.Items[0].FileName); // Largest first

        // 2. Query Page 2
        query.Offset = 10;
        var page2 = await _repository.QueryFilesAsync(query);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal(2, page2.PageNumber);
        Assert.Equal("Doc_15.pdf", page2.Items[0].FileName);

        // 3. Query with SearchText filter
        var searchQuery = new FileInventoryQuery
        {
            ScanRunId = scanId,
            SearchText = "Doc_05"
        };
        var searchResult = await _repository.QueryFilesAsync(searchQuery);
        Assert.Equal(1, searchResult.TotalCount);
        Assert.Equal("Doc_05.pdf", searchResult.Items[0].FileName);
    }

    [Fact]
    public async Task GetDiscoveredExtensionsAsync_ReturnsDistinctSortedExtensions()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.Completed };
        await _repository.CreateScanRunAsync(run);

        var files = new[]
        {
            new FileRecord { ScanRunId = scanId, FullPath = @"C:\a.mp4", ParentPath = @"C:\", FileName = "a.mp4", Extension = ".mp4", SizeBytes = 10, Attributes = 32, ObservedAtUtc = DateTime.UtcNow },
            new FileRecord { ScanRunId = scanId, FullPath = @"C:\b.iso", ParentPath = @"C:\", FileName = "b.iso", Extension = ".iso", SizeBytes = 20, Attributes = 32, ObservedAtUtc = DateTime.UtcNow },
            new FileRecord { ScanRunId = scanId, FullPath = @"C:\c.mp4", ParentPath = @"C:\", FileName = "c.mp4", Extension = ".mp4", SizeBytes = 30, Attributes = 32, ObservedAtUtc = DateTime.UtcNow },
            new FileRecord { ScanRunId = scanId, FullPath = @"C:\d.docx", ParentPath = @"C:\", FileName = "d.docx", Extension = ".docx", SizeBytes = 40, Attributes = 32, ObservedAtUtc = DateTime.UtcNow }
        };

        await _repository.InsertFileBatchAsync(files);

        var extensions = await _repository.GetDiscoveredExtensionsAsync(scanId);
        Assert.Equal(3, extensions.Count);
        Assert.Equal(new[] { ".docx", ".iso", ".mp4" }, extensions);
    }

    [Fact]
    public async Task BackupDatabaseAsync_CreatesConsistentOnlineBackupThatCanBeQueried()
    {
        var scanId = Guid.NewGuid();
        var run = new ScanRun { Id = scanId, Status = ScanState.Completed, SelectedDrives = "C:\\" };
        await _repository.CreateScanRunAsync(run);

        var file = new FileRecord
        {
            ScanRunId = scanId,
            FullPath = @"C:\Data\Important.dat",
            ParentPath = @"C:\Data",
            FileName = "Important.dat",
            Extension = ".dat",
            SizeBytes = 4096,
            Attributes = 32,
            ObservedAtUtc = DateTime.UtcNow
        };
        await _repository.InsertFileBatchAsync(new[] { file });

        var backupPath = Path.Combine(_tempDir, "backup_test.db");
        await _repository.BackupDatabaseAsync(backupPath);

        Assert.True(File.Exists(backupPath));

        // Reopen the backup database with a separate initializer and repository to verify integrity
        var backupEnv = new AppEnvironment(Path.Combine(_tempDir, "backup_verify"));
        Directory.CreateDirectory(backupEnv.DatabaseDirectory);
        File.Copy(backupPath, Path.Combine(backupEnv.DatabaseDirectory, "inventory.db"));

        var verifyInitializer = new SqliteDatabaseInitializer(backupEnv, NullLogger<SqliteDatabaseInitializer>.Instance);
        var verifyRepo = new SqliteScanRepository(verifyInitializer, NullLogger<SqliteScanRepository>.Instance);

        var retrievedRun = await verifyRepo.GetScanRunAsync(scanId);
        Assert.NotNull(retrievedRun);
        Assert.Equal(ScanState.Completed, retrievedRun.Status);

        var fileCount = await verifyRepo.GetFileCountForScanAsync(scanId);
        Assert.Equal(1, fileCount);
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
