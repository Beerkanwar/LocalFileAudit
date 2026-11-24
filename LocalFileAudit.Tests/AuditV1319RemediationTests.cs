using System.Globalization;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Core.Validation;
using LocalFileAudit.Infrastructure.AI;
using LocalFileAudit.Infrastructure.Analysis;
using LocalFileAudit.Infrastructure.Archive;
using LocalFileAudit.Infrastructure.Configuration;
using LocalFileAudit.Infrastructure.Environment;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Reporting;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalFileAudit.Tests;

/// <summary>
/// Regression tests for the v1.3.19 audit remediation:
/// analysis cache staleness, culture-invariant CSV, AI Markdown sanitization,
/// PowerShell literal paths, resume de-duplication, and streaming export counts.
/// </summary>
public sealed class AuditV1319RemediationTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppEnvironment _env;
    private readonly SqliteDatabaseInitializer _init;
    private readonly SqliteScanRepository _repo;
    private readonly JsonConfigurationService _config;

    public AuditV1319RemediationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LocalFileAudit_V1319_" + Guid.NewGuid().ToString("N"));
        _env = new AppEnvironment(Path.Combine(_testDir, "appdata"));
        _init = new SqliteDatabaseInitializer(_env, NullLogger<SqliteDatabaseInitializer>.Instance);
        _init.InitializeAsync().GetAwaiter().GetResult();
        _repo = new SqliteScanRepository(_init, NullLogger<SqliteScanRepository>.Instance);
        _config = new JsonConfigurationService(_env, new ConfigurationValidator(), NullLogger<JsonConfigurationService>.Instance);
        _config.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private StorageAnalysisEngine NewEngine() =>
        new(_init, _repo, _config, NullLogger<StorageAnalysisEngine>.Instance);

    private async Task<Guid> CreateCompletedScanWithFileAsync(long sizeBytes, string fullPath = @"C:\Data\movie.bin")
    {
        var scanId = Guid.NewGuid();
        await _repo.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.Completed, SelectedDrives = @"C:\Data" });
        await _repo.InsertFileBatchAsync(new[]
        {
            new FileRecord
            {
                ScanRunId = scanId,
                FullPath = fullPath,
                Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                SizeBytes = sizeBytes,
                LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1)
            }
        });
        return scanId;
    }

    private async Task SetLargeFileThresholdAsync(long bytes)
    {
        var cfg = _config.Current;
        cfg.Analysis.LargeFileThresholdBytes = bytes;
        await _config.SaveAsync(cfg);
    }

    // ---------- Analysis cache staleness ----------

    [Fact]
    public async Task AnalyzeScanAsync_RecomputesCandidates_WhenLargeFileThresholdChanges()
    {
        var scanId = await CreateCompletedScanWithFileAsync(600L * 1024 * 1024);
        await SetLargeFileThresholdAsync(500L * 1024 * 1024);

        var engine = NewEngine();
        var first = await engine.AnalyzeScanAsync(scanId);
        Assert.Contains(first.ReviewCandidates, c => c.RuleId == "RULE_LARGE_FILE");

        // Raise threshold above the file size: previously, both the in-memory and the persisted
        // ScanMetrics/AnalysisResults caches kept returning the stale candidate forever.
        await SetLargeFileThresholdAsync(2000L * 1024 * 1024);

        var warm = await engine.AnalyzeScanAsync(scanId);
        Assert.DoesNotContain(warm.ReviewCandidates, c => c.RuleId == "RULE_LARGE_FILE");

        // Cold start (fresh engine) must not resurrect the stale persisted summary either
        var cold = await NewEngine().AnalyzeScanAsync(scanId);
        Assert.DoesNotContain(cold.ReviewCandidates, c => c.RuleId == "RULE_LARGE_FILE");
        Assert.Equal(CandidateRuleEvaluator.GetAnalysisFingerprint(_config.Current), cold.AnalysisFingerprint);

        var direct = await NewEngine().ClassifyCandidatesAsync(scanId);
        Assert.DoesNotContain(direct, c => c.RuleId == "RULE_LARGE_FILE");
    }

    [Fact]
    public async Task InvalidateCacheAsync_RemovesPersistedScanMetrics()
    {
        var scanId = await CreateCompletedScanWithFileAsync(10);
        var engine = NewEngine();
        await engine.AnalyzeScanAsync(scanId);
        Assert.NotNull(await _repo.GetScanMetricsAsync(scanId));

        await engine.InvalidateCacheAsync(scanId);

        Assert.Null(await _repo.GetScanMetricsAsync(scanId));
        Assert.Empty(await _repo.GetCachedAnalysisResultsAsync(scanId));
    }

    [Fact]
    public async Task GetCachedAnalysisResultsAsync_WithVersion_IgnoresRowsFromOtherVersions()
    {
        var scanId = await CreateCompletedScanWithFileAsync(10);
        var page = await _repo.QueryFilesAsync(new FileInventoryQuery { ScanRunId = scanId });
        var candidate = new CandidateClassification
        {
            FileRecordId = page.Items[0].Id,
            ScanRunId = scanId,
            Category = CandidateCategory.Investigate,
            RuleId = "RULE_LARGE_FILE",
            Explanation = "x"
        };

        await _repo.SaveAnalysisResultsAsync(scanId, new[] { candidate }, "1.3|1|1");

        Assert.Single(await _repo.GetCachedAnalysisResultsAsync(scanId, "1.3|1|1"));
        Assert.Empty(await _repo.GetCachedAnalysisResultsAsync(scanId, "1.3|2|2"));
    }

    // ---------- Culture-invariant CSV ----------

    [Fact]
    public async Task GenerateReportsAsync_WritesCultureInvariantCsv_UnderCommaDecimalCulture()
    {
        var scanId = await CreateCompletedScanWithFileAsync(1_572_864, @"C:\Data\notes.txt"); // 1.5 MB
        var engine = NewEngine();
        var generator = new ReportGenerator(engine, _repo, _env, NullLogger<ReportGenerator>.Instance, _config);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var result = await generator.GenerateReportsAsync(scanId);

            var extLines = await File.ReadAllLinesAsync(Path.Combine(result.OutputDirectory, "ExtensionSummary.csv"));
            Assert.Equal(6, extLines[1].Split(',').Length);
            Assert.Contains("1.50", extLines[1]);

            var invLines = await File.ReadAllLinesAsync(Path.Combine(result.OutputDirectory, "Inventory.csv"));
            Assert.Equal(8, invLines[1].Split(',').Length);
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", invLines[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task GenerateReportsAsync_MarkdownSummary_RendersPathsInCodeSpansWithoutDoubledBackslashes()
    {
        var scanId = await CreateCompletedScanWithFileAsync(2048, @"C:\Data\Sub Dir\report.txt");
        var generator = new ReportGenerator(NewEngine(), _repo, _env, NullLogger<ReportGenerator>.Instance, _config);

        var result = await generator.GenerateReportsAsync(scanId);
        var md = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "Summary.md"));

        // Backslash escapes are not processed inside code spans, so doubling them corrupts the displayed path
        Assert.Contains(@"`C:\Data\Sub Dir`", md);
        Assert.DoesNotContain(@"C:\\Data", md);
    }

    // ---------- Streaming export count ----------

    [Fact]
    public async Task QueryFilesAsync_WithoutTotalCount_ReturnsPageAndRowCount()
    {
        var scanId = await CreateCompletedScanWithFileAsync(10);
        var page = await _repo.QueryFilesAsync(new FileInventoryQuery
        {
            ScanRunId = scanId,
            SortBy = "Id",
            SortDescending = false,
            AfterId = 0,
            IncludeTotalCount = false
        });

        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    // ---------- AI Markdown sanitization ----------

    [Fact]
    public void SanitizeMarkdownBody_NeutralizesImagesLinksAndReferenceDefinitions()
    {
        var input = "Look ![a][r] and ![b](https://evil.example/x.png) [c]( HTTPS://evil.example) [d](//evil.example)\n[r]: https://evil.example/leak?q=secret";

        var output = AiAnalysisService.SanitizeMarkdownBody(input);

        Assert.DoesNotContain("![", output);
        Assert.DoesNotContain("evil.example", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EscapeMarkdownTableCell_EncodesRawHtml()
    {
        var output = AiAnalysisService.EscapeMarkdownTableCell("<img src=\"https://evil.example/p\"> | ![x](y)");

        Assert.DoesNotContain("<img", output);
        Assert.Contains("&lt;img", output);
        Assert.Contains("\\|", output);
        Assert.DoesNotContain("![", output);
    }

    [Fact]
    public async Task ExportAiReviewTableAsync_SanitizesReviewPlanAndWarnings()
    {
        var service = new AiAnalysisService(
            new NoopOllamaClient(), NewEngine(), _repo, _env, _config, NullLogger<AiAnalysisService>.Instance);

        var response = new AiAnalysisResponse
        {
            PrioritizedReviewPlan = new[] { "Step ![t](https://evil.example/t.png) <img src=https://evil.example/i>" },
            SafetyWarnings = new[] { "[click](javascript:alert(1))" },
            Recommendations = new[]
            {
                new AiRecommendation { CandidateId = 1, FileName = "a.iso", Explanation = "<script>x</script>" }
            }
        };

        var (_, mdPath) = await service.ExportAiReviewTableAsync(Guid.NewGuid(), "model", response);
        var md = await File.ReadAllTextAsync(mdPath);

        Assert.DoesNotContain("evil.example", md);
        Assert.DoesNotContain("javascript:", md);
        Assert.DoesNotContain("<script>", md);
        Assert.DoesNotContain("<img", md);
    }

    // ---------- PowerShell literal paths ----------

    [Fact]
    public async Task GenerateArchiveScriptsAsync_UsesLiteralPathForBracketSafePathChecks()
    {
        var planner = new ArchivePlannerService(_env, NullLogger<ArchivePlannerService>.Instance);
        var plan = new ArchivePlan
        {
            Batches =
            {
                new ArchiveBatch
                {
                    BatchName = "Photos",
                    SourcePath = @"C:\Photos [2020]",
                    PlannedDestinationPath = @"E:\Archive [2020]"
                }
            }
        };

        var result = await planner.GenerateArchiveScriptsAsync(plan, Path.Combine(_testDir, "scripts"));

        foreach (var script in new[] { result.PreviewScriptPath, result.CopyScriptPath })
        {
            var text = await File.ReadAllTextAsync(script);
            Assert.Contains("Test-Path -LiteralPath $src", text);
            Assert.DoesNotContain("Test-Path $src", text);
            Assert.DoesNotContain("Test-Path $dst", text);
            Assert.DoesNotContain("Test-Path $logDir", text);
        }
    }

    // ---------- Resume de-duplication ----------

    [Fact]
    public async Task InsertDirectoryBatchAsync_OnConflict_ReturnsPersistedStatus()
    {
        var scanId = Guid.NewGuid();
        await _repo.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.InProgress });
        var first = new DirectoryRecord { ScanRunId = scanId, Path = @"C:\X\Done" };
        await _repo.InsertDirectoryBatchAsync(new[] { first });
        await _repo.UpdateDirectoryStatusAsync(first.Id, DirectoryState.Completed, 3, null);

        var again = new DirectoryRecord { ScanRunId = scanId, Path = @"C:\X\Done", Status = DirectoryState.Pending };
        await _repo.InsertDirectoryBatchAsync(new[] { again });

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(DirectoryState.Completed, again.Status);
    }

    [Fact]
    public async Task ResumeScanAsync_DoesNotRetraverseCompletedSubdirectories()
    {
        var root = Path.Combine(_testDir, "src");
        var done = Path.Combine(root, "AlreadyDone");
        var fresh = Path.Combine(root, "Fresh");
        Directory.CreateDirectory(done);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(root, "root.txt"), "r");
        File.WriteAllText(Path.Combine(done, "done.txt"), "d");
        File.WriteAllText(Path.Combine(fresh, "fresh.txt"), "f");

        // Simulate a crashed session: root still Pending, "AlreadyDone" was fully processed earlier.
        // Its file is deliberately absent from the DB so any re-traversal would be observable.
        var scanId = Guid.NewGuid();
        await _repo.CreateScanRunAsync(new ScanRun { Id = scanId, Status = ScanState.InProgress, SelectedDrives = root });
        var rootDir = new DirectoryRecord { ScanRunId = scanId, Path = root };
        var doneDir = new DirectoryRecord { ScanRunId = scanId, Path = done, ParentPath = root };
        await _repo.InsertDirectoryBatchAsync(new[] { rootDir, doneDir });
        await _repo.UpdateDirectoryStatusAsync(doneDir.Id, DirectoryState.Completed, 1, null);

        var scanner = new FilesystemScanner(_repo, _config, NullLogger<FilesystemScanner>.Instance);
        var run = await scanner.ResumeScanAsync(scanId, CancellationToken.None);

        Assert.Equal(ScanState.Completed, run.Status);
        var files = await _repo.QueryFilesAsync(new FileInventoryQuery { ScanRunId = scanId, Limit = 100 });
        var names = files.Items.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("root.txt", names);
        Assert.Contains("fresh.txt", names);
        Assert.DoesNotContain("done.txt", names);
    }

    private sealed class NoopOllamaClient : IOllamaClient
    {
        public Task<AiConnectionStatus> CheckConnectionAsync(string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiConnectionStatus { IsAvailable = false, Endpoint = endpoint });

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<string> GenerateJsonAsync(string endpoint, string model, string systemPrompt, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");

        public Task<string> SendChatAsync(string endpoint, string model, string systemPrompt, IEnumerable<ChatMessageRecord> history, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
