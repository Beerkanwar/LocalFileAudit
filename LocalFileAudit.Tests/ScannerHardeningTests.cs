using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalFileAudit.Tests;

public class ScannerHardeningTests : IDisposable
{
    private readonly string _tempAppDataDir;
    private readonly string _testSourceDir;
    private readonly AppEnvironment _environment;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly SqliteScanRepository _repository;
    private readonly JsonConfigurationService _configService;
    private readonly FilesystemScanner _scanner;

    public ScannerHardeningTests()
    {
        _tempAppDataDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_HardenApp_" + Guid.NewGuid().ToString("N"));
        _testSourceDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_HardenSrc_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempAppDataDir);
        Directory.CreateDirectory(_testSourceDir);

        _environment = new AppEnvironment(_tempAppDataDir);
        _initializer = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        _repository = new SqliteScanRepository(_initializer, NullLogger<SqliteScanRepository>.Instance);
        _configService = new JsonConfigurationService(_environment, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _scanner = new FilesystemScanner(_repository, _configService, NullLogger<FilesystemScanner>.Instance);

        _initializer.InitializeAsync().GetAwaiter().GetResult();
        _configService.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempAppDataDir)) Directory.Delete(_tempAppDataDir, recursive: true); } catch { }
        try { if (Directory.Exists(_testSourceDir)) Directory.Delete(_testSourceDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Scan_EmptyDirectories_HandledCleanlyWithoutErrors()
    {
        // Hierarchy of empty directories
        var dirA = Path.Combine(_testSourceDir, "EmptyA");
        var dirB = Path.Combine(_testSourceDir, "EmptyB");
        var dirNested = Path.Combine(dirA, "NestedEmpty");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(dirNested);

        var target = new ScanTarget { TargetPaths = new List<string> { _testSourceDir } };
        var run = await _scanner.StartScanAsync(target, CancellationToken.None);

        Assert.Equal(ScanState.Completed, run.Status);
        Assert.Equal(0, run.FilesDiscovered);
        Assert.Equal(0, run.ErrorCount);

        var dbCount = await _repository.GetFileCountForScanAsync(run.Id);
        Assert.Equal(0, dbCount);
    }

    [Fact]
    public async Task Scan_UnicodeFilenamesAndDirectories_PersistedAndRetrievedAccurately()
    {
        // Unicode filenames: Japanese, Cyrillic, Umlauts, Emoji
        var unicodeDir = Path.Combine(_testSourceDir, "📁_フォルダ_папка");
        Directory.CreateDirectory(unicodeDir);

        var file1 = Path.Combine(unicodeDir, "ドキュメント_日本語.txt");
        var file2 = Path.Combine(unicodeDir, "отчет_за_год.pdf");
        var file3 = Path.Combine(unicodeDir, "münchen_übung.doc");
        var file4 = Path.Combine(unicodeDir, "🚀_space_rocket.json");

        await File.WriteAllTextAsync(file1, "Japanese content");
        await File.WriteAllTextAsync(file2, "Cyrillic content");
        await File.WriteAllTextAsync(file3, "German content");
        await File.WriteAllTextAsync(file4, "Emoji content");

        var target = new ScanTarget { TargetPaths = new List<string> { _testSourceDir } };
        var run = await _scanner.StartScanAsync(target, CancellationToken.None);

        Assert.Equal(ScanState.Completed, run.Status);
        Assert.Equal(4, run.FilesDiscovered);

        var query = await _repository.QueryFilesAsync(new FileInventoryQuery { ScanRunId = run.Id, Limit = 10 });
        Assert.Equal(4, query.TotalCount);

        var names = query.Items.Select(f => f.FileName).ToList();
        Assert.Contains("ドキュメント_日本語.txt", names);
        Assert.Contains("отчет_за_год.pdf", names);
        Assert.Contains("münchen_übung.doc", names);
        Assert.Contains("🚀_space_rocket.json", names);
    }

    [Fact]
    public async Task Scan_DeeplyNestedDirectories_DiscoversAllFiles()
    {
        var current = _testSourceDir;
        for (int i = 1; i <= 6; i++)
        {
            current = Path.Combine(current, $"Level_{i}");
            Directory.CreateDirectory(current);
            await File.WriteAllTextAsync(Path.Combine(current, $"file_level_{i}.txt"), $"Content {i}");
        }

        var target = new ScanTarget { TargetPaths = new List<string> { _testSourceDir } };
        var run = await _scanner.StartScanAsync(target, CancellationToken.None);

        Assert.Equal(ScanState.Completed, run.Status);
        Assert.Equal(6, run.FilesDiscovered);

        var dbCount = await _repository.GetFileCountForScanAsync(run.Id);
        Assert.Equal(6, dbCount);
    }
}
