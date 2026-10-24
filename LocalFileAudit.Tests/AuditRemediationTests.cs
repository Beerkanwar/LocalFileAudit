using System.Text.Json;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Logging;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public sealed class AuditRemediationTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppEnvironment _environment;

    public AuditRemediationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_AuditRemediation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _environment = new AppEnvironment(_testDir);
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
            // Best effort
        }
    }

    [Fact]
    public void H3_PathOverlapValidator_DetectsNestedAndAliasedPaths()
    {
        var src = Path.Combine(_testDir, "Source");
        var nestedDst = Path.Combine(src, "NestedBackup");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(nestedDst);

        // Nested inside source must throw
        Assert.Throws<InvalidOperationException>(() =>
            PathOverlapValidator.ValidateDoNotOverlap(src, nestedDst));

        // Source equals destination must throw
        Assert.Throws<InvalidOperationException>(() =>
            PathOverlapValidator.ValidateDoNotOverlap(src, src));

        // Relative path resolution inside source must throw
        var relativeNested = Path.Combine(_testDir, "Source", "Sub", "..", "Sub2");
        Directory.CreateDirectory(relativeNested);
        Assert.Throws<InvalidOperationException>(() =>
            PathOverlapValidator.ValidateDoNotOverlap(src, relativeNested));

        // Separate directories must pass
        var validDst = Path.Combine(_testDir, "ValidBackup");
        Directory.CreateDirectory(validDst);
        PathOverlapValidator.ValidateDoNotOverlap(src, validDst);
    }

    [Fact]
    public void H8_Scanner_PruneNestedRoots_CanonicalizesAndDropsNested()
    {
        var rootA = Path.Combine(_testDir, "DriveA");
        var subA = Path.Combine(rootA, "SubFolder");
        var rootB = Path.Combine(_testDir, "DriveB");

        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(rootB);

        var input = new[] { rootA, subA, rootA + Path.DirectorySeparatorChar, rootB };
        var pruned = FilesystemScanner.PruneNestedRoots(input);

        Assert.Equal(2, pruned.Count);
        Assert.Contains(Path.GetFullPath(rootA), pruned);
        Assert.Contains(Path.GetFullPath(rootB), pruned);
        Assert.DoesNotContain(Path.GetFullPath(subA), pruned);
    }

    [Fact]
    public void L6_CsvSanitizer_DoesNotCorruptNumericValues()
    {
        // Valid negative and positive numbers must NOT have leading single quotes
        Assert.Equal("-42", CsvSanitizer.EscapeCsv("-42"));
        Assert.Equal("-1024.50", CsvSanitizer.EscapeCsv("-1024.50"));
        Assert.Equal("+99", CsvSanitizer.EscapeCsv("+99"));

        // Dangerous formulas must have leading single quotes for spreadsheet security
        Assert.Equal("'-cmd", CsvSanitizer.EscapeCsv("-cmd"));
        Assert.Equal("'=SUM(A1:A10)", CsvSanitizer.EscapeCsv("=SUM(A1:A10)"));
        Assert.Equal("'+calc", CsvSanitizer.EscapeCsv("+calc"));
        Assert.Equal("'@evil", CsvSanitizer.EscapeCsv("@evil"));
    }

    [Fact]
    public void L11_CandidateRuleEvaluator_ProtectsSystemAndAppDataFiles()
    {
        var config = new AppConfiguration();

        var pagefile = new FileRecord
        {
            FullPath = @"C:\pagefile.sys",
            FileName = "pagefile.sys",
            Extension = ".sys",
            SizeBytes = 4_000_000_000
        };
        var pagefileClass = CandidateRuleEvaluator.Classify(pagefile, config);
        Assert.Equal("RULE_PROTECTED_SYSTEM", pagefileClass.RuleId);
        Assert.Equal(CandidateCategory.ProtectedSystem, pagefileClass.Category);

        var appDataFile = new FileRecord
        {
            FullPath = @"C:\Users\TestUser\AppData\Local\Temp\cache.tmp",
            FileName = "cache.tmp",
            Extension = ".tmp",
            SizeBytes = 10_000_000
        };
        var appDataClass = CandidateRuleEvaluator.Classify(appDataFile, config);
        Assert.Equal("RULE_PROTECTED_SYSTEM", appDataClass.RuleId);

        var vhdxFile = new FileRecord
        {
            FullPath = @"C:\Users\TestUser\AppData\Local\Docker\wsl\data\ext4.vhdx",
            FileName = "ext4.vhdx",
            Extension = ".vhdx",
            SizeBytes = 20_000_000_000
        };
        var vhdxClass = CandidateRuleEvaluator.Classify(vhdxFile, config);
        Assert.Equal("RULE_PROTECTED_SYSTEM", vhdxClass.RuleId);
    }

    [Fact]
    public void M2_PathRedactor_RedactsIntermediateDirectoryStructures()
    {
        var path = @"D:\Clients\AcmeCorp\Financials\2025";
        var redacted = PathRedactor.RedactDirectory(path);

        Assert.NotNull(redacted);
        Assert.DoesNotContain("AcmeCorp", redacted);
        Assert.Contains("...", redacted);
    }

    [Fact]
    public async Task M7_KeysetPagination_QueriesFilesAfterId()
    {
        var init = new SqliteDatabaseInitializer(_environment, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);

        var scanId = Guid.NewGuid();
        var run = new ScanRun
        {
            Id = scanId,
            StartTimeUtc = DateTime.UtcNow,
            Status = ScanState.Completed,
            SelectedDrives = _testDir
        };
        await repo.CreateScanRunAsync(run);

        var files = Enumerable.Range(1, 25).Select(i => new FileRecord
        {
            ScanRunId = scanId,
            FullPath = Path.Combine(_testDir, $"file_{i:D2}.dat"),
            ParentPath = _testDir,
            FileName = $"file_{i:D2}.dat",
            Extension = ".dat",
            SizeBytes = i * 100,
            ObservedAtUtc = DateTime.UtcNow
        }).ToList();
        await repo.InsertFileBatchAsync(files);

        // First page: fetch first 10 items
        var query1 = new FileInventoryQuery
        {
            ScanRunId = scanId,
            Limit = 10,
            SortBy = "Id",
            SortDescending = false
        };
        var page1 = await repo.QueryFilesAsync(query1);
        Assert.Equal(10, page1.Items.Count);

        // Second page: keyset paging using AfterId
        var lastId = page1.Items.Last().Id;
        var query2 = new FileInventoryQuery
        {
            ScanRunId = scanId,
            Limit = 10,
            AfterId = lastId,
            SortBy = "Id",
            SortDescending = false
        };
        var page2 = await repo.QueryFilesAsync(query2);
        Assert.Equal(10, page2.Items.Count);
        Assert.True(page2.Items.First().Id > lastId);
    }

    [Fact]
    public async Task M19_ConfigurationService_PerFieldRepairPreservesValidUserProperties()
    {
        var validator = new ConfigurationValidator();
        var configService = new JsonConfigurationService(_environment, validator, NullLogger<JsonConfigurationService>.Instance);

        // Write an invalid config to disk (e.g., negative DatabaseBatchSize, but custom RetainDays)
        var invalidConfig = new AppConfiguration
        {
            Scanning = new ScanningConfiguration
            {
                DatabaseBatchSize = -999, // Invalid!
                ExcludedPaths = new List<string> { @"C:\CustomExcludedFolder" }
            },
            Logging = new LoggingConfiguration
            {
                RetainDays = 45,
                LogLevel = "Warning"
            }
        };

        var json = JsonSerializer.Serialize(invalidConfig, new JsonSerializerOptions { WriteIndented = true });
        _environment.EnsureDirectoriesCreated();
        await File.WriteAllTextAsync(_environment.ConfigFilePath, json);

        // Loading should repair DatabaseBatchSize to safe default while preserving custom RetainDays and ExcludedPaths
        var loaded = await configService.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(500, loaded.Scanning.DatabaseBatchSize); // Repaired to default
        Assert.Equal(45, loaded.Logging.RetainDays); // Preserved
        Assert.Equal("Warning", loaded.Logging.LogLevel); // Preserved
        Assert.Contains(@"C:\CustomExcludedFolder", loaded.Scanning.ExcludedPaths); // Preserved
    }
}
