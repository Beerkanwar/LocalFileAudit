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
