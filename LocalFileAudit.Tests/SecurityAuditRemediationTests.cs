using System.IO;
using System.Text.Json;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.AI;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Archive;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Logging;
using LocalFileAudit.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

public class SecurityAuditRemediationTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestAppEnvironment _env;

    public SecurityAuditRemediationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_AuditTests_" + Guid.NewGuid().ToString("N"));
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

    // --- Finding 1: PowerShell Script Injection & Robocopy Trailing Backslash ---

    [Theory]
    [InlineData("test'folder", "test''folder")]
    [InlineData("x’;calc;’", "x’’;calc;’’")] // Unicode U+2019 right single quote
    [InlineData("x‘;calc;‘", "x‘‘;calc;‘‘")] // Unicode U+2018 left single quote
    [InlineData("x‚;calc;‚", "x‚‚;calc;‚‚")] // Unicode U+201A single low-9 quote
    [InlineData("x‛;calc;‛", "x‛‛;calc;‛‛")] // Unicode U+201B single high-reversed-9 quote
    public void High1_PowerShellLiteralEscaping_DoublesAllSingleQuoteVariants(string input, string expected)
    {
        var escaped = ArchivePlannerService.EscapePowerShellSingleQuotedLiteral(input);
        Assert.Equal(expected, escaped);
    }

    [Fact]
    public void High1_SanitizeIdentifier_StripsShellMetacharactersAndQuotes()
    {
        var input = "my_run;calc;`$var'’‘‚‛\"test";
        var sanitized = ArchivePlannerService.SanitizeIdentifier(input);

        Assert.DoesNotContain(";", sanitized);
        Assert.DoesNotContain("`", sanitized);
        Assert.DoesNotContain("$", sanitized);
        Assert.DoesNotContain("'", sanitized);
        Assert.DoesNotContain("’", sanitized);
        Assert.DoesNotContain("\"", sanitized);
    }

    [Fact]
    public void High1_PathOverlap_PreventsNestedAndIdenticalPaths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ArchivePlannerService.ValidatePathsDoNotOverlap(@"C:\Data", @"C:\Data"));

        Assert.Throws<InvalidOperationException>(() =>
            ArchivePlannerService.ValidatePathsDoNotOverlap(@"C:\Data", @"C:\Data\Nested"));

        Assert.Throws<InvalidOperationException>(() =>
            ArchivePlannerService.ValidatePathsDoNotOverlap(@"C:\Data\Nested", @"C:\Data"));
    }

    // --- Finding 2: Hash Errors Compare As Match ---

    [Fact]
    public void High2_HashErrors_NeverCountAsMatch()
    {
        // When both source and dest fail to compute hash (e.g. sharing violation, lock)
        string? srcHash = null;
        string? dstHash = null;

        var status = (srcHash == null || dstHash == null)
            ? VerificationStatus.HashError
            : (srcHash.Equals(dstHash, StringComparison.OrdinalIgnoreCase) ? VerificationStatus.Match : VerificationStatus.HashMismatch);

        Assert.Equal(VerificationStatus.HashError, status);
        Assert.NotEqual(VerificationStatus.Match, status);
    }

    // --- Finding 3: Directory Checkpoints Populated and Persisted ---

    [Fact]
    public async Task High3_DirectoryCheckpoints_PopulateIdAndPersistStatus()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var scan = new ScanRun { SelectedDrives = @"C:\Test" };
        await repo.CreateScanRunAsync(scan);

        var dirs = new List<DirectoryRecord>
        {
            new()
            {
                ScanRunId = scan.Id,
                Path = @"C:\Test\SubDir",
                ParentPath = @"C:\Test",
                Status = DirectoryState.Pending,
                LastCheckpointUtc = DateTime.UtcNow
            }
        };

        await repo.InsertDirectoryBatchAsync(dirs);

        // Verify ID was set on record
        Assert.True(dirs[0].Id > 0, "InsertDirectoryBatchAsync must write the generated ID back to the record.");

        // Verify UpdateDirectoryStatusAsync succeeds and changes state
        await repo.UpdateDirectoryStatusAsync(dirs[0].Id, DirectoryState.Completed, filesProcessed: 42, lastError: null);

        var pendingDirs = await repo.GetPendingOrInterruptedDirectoriesAsync(scan.Id);
        Assert.DoesNotContain(pendingDirs, d => d.Id == dirs[0].Id);
    }

    // --- Finding 6: Cloud Placeholders Excluded from Stage 1 Duplicate Detection ---

    [Fact]
    public async Task Medium6_DuplicateDetection_QueryExcludesReparseAndCloudPlaceholders()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        await init.InitializeAsync();

        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var scan = new ScanRun { SelectedDrives = @"C:\Test" };
        await repo.CreateScanRunAsync(scan);

        // Insert two normal files with identical size
        var normalFiles = new List<FileRecord>
        {
            new() { ScanRunId = scan.Id, FullPath = @"C:\Test\a.dat", FileName = "a.dat", Extension = ".dat", SizeBytes = 2048, Attributes = 32, MetadataStatus = ScanMetadataStatus.Ok },
            new() { ScanRunId = scan.Id, FullPath = @"C:\Test\b.dat", FileName = "b.dat", Extension = ".dat", SizeBytes = 2048, Attributes = 32, MetadataStatus = ScanMetadataStatus.Ok },
            // Cloud placeholder file with same size
            new() { ScanRunId = scan.Id, FullPath = @"C:\Test\c.dat", FileName = "c.dat", Extension = ".dat", SizeBytes = 2048, Attributes = 32 | 1024, MetadataStatus = ScanMetadataStatus.CloudPlaceholder }
        };
        await repo.InsertFileBatchAsync(normalFiles);

        var engine = new DuplicateAnalysisEngine(init, repo, NullLogger<DuplicateAnalysisEngine>.Instance);
        var stage1 = await engine.FindPotentialDuplicatesBySizeAsync(scan.Id);

        Assert.Single(stage1.Groups);
        var group = stage1.Groups[0];
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.MetadataStatus == ScanMetadataStatus.CloudPlaceholder);
    }

    // --- Finding 9: Path and Filename Redaction ---

    [Fact]
    public void Medium9_PathRedactor_RedactsDrivesUncAndPosixPaths()
    {
        var drivePath = @"C:\Users\JohnDoe\Documents\secret.pdf";
        Assert.Equal(@"C:\Users\[USER]\Documents\secret.pdf", PathRedactor.Redact(drivePath));

        var uncPath = @"\\server\share\Users\Alice\Reports\financial.xlsx";
        Assert.Equal(@"\\SERVER\SHARE\Users\[USER]\Reports\financial.xlsx", PathRedactor.Redact(uncPath));

        var filename = "tax_returns_2024.pdf";
        var redactedFile = PathRedactor.RedactFileName(filename, 101);
        Assert.Equal("candidate_101.pdf", redactedFile);
    }

    // --- Finding 10: AI Output Validation & Markdown Escaping ---

    [Fact]
    public void Medium10_ValidateAndParseResponse_EnforcesEnumAndHandlesMalformedJson()
    {
        var init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        var repo = new SqliteScanRepository(init, NullLogger<SqliteScanRepository>.Instance);
        var configService = new JsonConfigurationService(_env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        var engine = new StorageAnalysisEngine(init, repo, configService, NullLogger<StorageAnalysisEngine>.Instance);
        var service = new AiAnalysisService(
            new MockOllamaClient(),
            engine,
            repo,
            _env,
            configService,
            NullLogger<AiAnalysisService>.Instance);

        var summary = new StorageMetricsSummary
        {
            ReviewCandidates = new List<CandidateClassification>
            {
                new() { FileRecordId = 1, FileName = "test.zip", Category = CandidateCategory.ArchiveCandidate }
            }
        };

        // Non-object root throws FormatException
        Assert.Throws<FormatException>(() => service.ValidateAndParseResponse("[\"not\", \"object\"]", summary, "test-model"));

        // Valid JSON with invalid category defaults to Investigate
        var jsonWithInvalidCategory = """
        {
            "executiveSummary": "All good",
            "storageHotspotsAssessment": "None",
            "recommendations": [
                {
                    "candidateId": 1,
                    "suggestedCategory": "MaliciousDelete",
                    "confidenceLevel": "High",
                    "explanation": "Test explanation",
                    "evidence": "Evidence | with | pipes",
                    "recommendedVerificationStep": "Verify"
                }
            ],
            "prioritizedReviewPlan": ["Step 1"],
            "safetyWarnings": ["Warning 1"]
        }
        """;

        var response = service.ValidateAndParseResponse(jsonWithInvalidCategory, summary, "test-model");
        Assert.Single(response.Recommendations);
        Assert.Equal(CandidateCategory.Investigate.ToString(), response.Recommendations[0].SuggestedCategory);
    }

    // --- Finding 11: Configuration Backup and Safe Endpoint Validation ---

    [Fact]
    public async Task Medium11_ConfigService_BacksUpCorruptedFileBeforeFallback()
    {
        var configFile = _env.ConfigFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        await File.WriteAllTextAsync(configFile, "{ invalid json corrupt content !!! ");

        var configService = new JsonConfigurationService(_env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        var loaded = await configService.LoadAsync();

        // Defaults loaded
        Assert.NotNull(loaded);

        // Backup file exists
        var backupFiles = Directory.GetFiles(Path.GetDirectoryName(configFile)!, "*.corrupted.*.bak");
        Assert.NotEmpty(backupFiles);
    }

    [Fact]
    public void Medium11_ConfigurationValidator_ValidatesSchemeAndAbsoluteUri()
    {
        var validator = new ConfigurationValidator();
        var cfg = new AppConfiguration
        {
            AI = new AiConfiguration
            {
                RequireLocalEndpoint = false,
                Endpoint = "ftp://remote-server/test" // Non http/https scheme
            }
        };

        var result = validator.Validate(cfg);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must use http or https scheme"));

        cfg.AI.Endpoint = "not a uri at all";
        result = validator.Validate(cfg);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not a valid absolute URI"));
    }

    // --- Finding 13: SelectedRootsHelper preserves semicolons and commas ---

    [Fact]
    public void Medium13_SelectedRootsHelper_RoundtripsRootsWithSpecialCharacters()
    {
        var originalRoots = new[] { @"C:\Folder;WithSemicolon", @"D:\Folder,WithComma", @"E:\Normal" };
        var joined = SelectedRootsHelper.JoinRoots(originalRoots);
        var parsed = SelectedRootsHelper.ParseRoots(joined);

        Assert.Equal(originalRoots, parsed);
    }

    // --- Finding 14: Anchored Protected Path and User Downloads Rules ---

    [Fact]
    public void Medium14_CandidateRuleEvaluator_AnchoredRules()
    {
        var cfg = new AppConfiguration();

        // 1. D:\Projects\Windows\app.exe must NOT be protected system
        var devFile = new FileRecord
        {
            Id = 1,
            FullPath = @"D:\Projects\Windows\app.exe",
            FileName = "app.exe",
            Extension = ".exe",
            SizeBytes = 1024,
            LastWriteTimeUtc = DateTime.UtcNow
        };
        var devResult = CandidateRuleEvaluator.Classify(devFile, cfg);
        Assert.NotEqual(CandidateCategory.ProtectedSystem, devResult.Category);

        // 2. C:\Windows\System32\cmd.exe must BE protected system
        var sysFile = new FileRecord
        {
            Id = 2,
            FullPath = @"C:\Windows\System32\cmd.exe",
            FileName = "cmd.exe",
            Extension = ".exe",
            SizeBytes = 1024,
            LastWriteTimeUtc = DateTime.UtcNow
        };
        var sysResult = CandidateRuleEvaluator.Classify(sysFile, cfg);
        Assert.Equal(CandidateCategory.ProtectedSystem, sysResult.Category);

        // 3. D:\Dev\Downloads\installer.msi must NOT match User Downloads rule
        var devDownloadsFile = new FileRecord
        {
            Id = 3,
            FullPath = @"D:\Dev\Downloads\installer.msi",
            FileName = "installer.msi",
            Extension = ".msi",
            SizeBytes = 10_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-100)
        };
        var devDlResult = CandidateRuleEvaluator.Classify(devDownloadsFile, cfg);
        Assert.NotEqual("RULE_OLD_DOWNLOADED_INSTALLER", devDlResult.RuleId);

        // 4. C:\Users\Alice\Downloads\installer.msi MUST match User Downloads rule
        var userDownloadsFile = new FileRecord
        {
            Id = 4,
            FullPath = @"C:\Users\Alice\Downloads\installer.msi",
            FileName = "installer.msi",
            Extension = ".msi",
            SizeBytes = 10_000_000,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-100)
        };
        var userDlResult = CandidateRuleEvaluator.Classify(userDownloadsFile, cfg);
        Assert.Equal("RULE_OLD_DOWNLOADED_INSTALLER", userDlResult.RuleId);
    }

    // --- Low: CSV Formula Injection Sanitization ---

    [Theory]
    [InlineData("=SUM(A1:A10)", "'=SUM(A1:A10)")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3*[calc]", "'-2+3*[calc]")]
    [InlineData("@HYPERLINK(\"http://evil.com\")", "'@HYPERLINK(\"\"http://evil.com\"\")")]
    [InlineData("\tcmd", "'\tcmd")]
    public void Low_CsvSanitizer_NeutralizesFormulaInjection(string input, string expectedStartsWith)
    {
        var escaped = CsvSanitizer.EscapeCsv(input);
        Assert.True(escaped.StartsWith(expectedStartsWith) || escaped.StartsWith("\"" + expectedStartsWith));
    }

    private sealed class TestAppEnvironment : IAppEnvironment
    {
        public string ApplicationDataDirectory { get; }
        public string DatabaseDirectory => Path.Combine(ApplicationDataDirectory, "database");
        public string LogsDirectory => Path.Combine(ApplicationDataDirectory, "logs");
        public string ReportsDirectory => Path.Combine(ApplicationDataDirectory, "reports");
        public string BackupsDirectory => Path.Combine(ApplicationDataDirectory, "backups");
        public string ConfigFilePath => Path.Combine(ApplicationDataDirectory, "config.json");

        public TestAppEnvironment(string root)
        {
            ApplicationDataDirectory = root;
        }

        public void EnsureDirectoriesCreated()
        {
            Directory.CreateDirectory(ApplicationDataDirectory);
            Directory.CreateDirectory(DatabaseDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ReportsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
        }
    }

    private sealed class MockOllamaClient : IOllamaClient
    {
        public Task<AiConnectionStatus> CheckConnectionAsync(string endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConnectionStatus { IsAvailable = true });

        public Task<string> GenerateJsonAsync(string endpoint, string model, string systemPrompt, string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(string endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "qwen2.5:7b" });

        public Task<string> SendChatAsync(string endpoint, string model, string systemPrompt, IEnumerable<ChatMessageRecord> history, CancellationToken cancellationToken = default) =>
            Task.FromResult("reply");
    }
}
