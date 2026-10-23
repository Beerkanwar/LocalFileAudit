using System.Net;
using System.Text;
using System.Text.Json;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Reporting;

/// <summary>
/// Generates deterministic audit reports atomically without AI dependencies.
/// Strictly escapes all user-controlled text in HTML outputs.
/// </summary>
public sealed class ReportGenerator : IReportGenerator
{
    private readonly IStorageAnalysisEngine _analysisEngine;
    private readonly IScanRepository _repository;
    private readonly IAppEnvironment _environment;
    private readonly ILogger<ReportGenerator> _logger;
    private readonly IConfigurationService? _configurationService;

    public ReportGenerator(
        IStorageAnalysisEngine analysisEngine,
        IScanRepository repository,
        IAppEnvironment environment,
        ILogger<ReportGenerator> logger,
        IConfigurationService? configurationService = null)
    {
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configurationService = configurationService;
    }

    public async Task<ReportGenerationResult> GenerateReportsAsync(
        Guid scanRunId,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetScanRunAsync(scanRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Scan run {scanRunId} not found.", nameof(scanRunId));

        var metrics = await _analysisEngine.AnalyzeScanAsync(scanRunId, cancellationToken).ConfigureAwait(false);
        var errors = await _repository.GetScanErrorsAsync(scanRunId, 500, cancellationToken).ConfigureAwait(false);

        var outputDir = targetDirectory ?? Path.Combine(
            _environment.ReportsDirectory,
            $"scan-{scanRunId:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");

        var parentReportsDir = Path.GetDirectoryName(outputDir) ?? _environment.ReportsDirectory;
        Directory.CreateDirectory(parentReportsDir);
        var stagingDir = Path.Combine(parentReportsDir, $".staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        _logger.LogInformation("Generating reports for scan {ScanRunId} into staging {StagingDir}", scanRunId, stagingDir);

        var stagedFileNames = new List<string>();
        try
        {
            // 1. Summary.md
            var mdStaging = Path.Combine(stagingDir, "Summary.md");
            await File.WriteAllTextAsync(mdStaging, GenerateMarkdownSummary(run, metrics), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("Summary.md");

            // 2. Summary.html (Secure, local-only, escaped HTML)
            var htmlStaging = Path.Combine(stagingDir, "Summary.html");
            await File.WriteAllTextAsync(htmlStaging, GenerateHtmlSummary(run, metrics), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("Summary.html");

            // 3. ExtensionSummary.csv
            var extStaging = Path.Combine(stagingDir, "ExtensionSummary.csv");
            await File.WriteAllTextAsync(extStaging, GenerateExtensionCsv(metrics.TopExtensions), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("ExtensionSummary.csv");

            // 4. DirectorySummary.csv
            var dirStaging = Path.Combine(stagingDir, "DirectorySummary.csv");
            await File.WriteAllTextAsync(dirStaging, GenerateDirectoryCsv(metrics.LargestDirectories), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("DirectorySummary.csv");

            // 5. ReviewCandidates.csv
            var candidatesStaging = Path.Combine(stagingDir, "ReviewCandidates.csv");
            await File.WriteAllTextAsync(candidatesStaging, GenerateCandidatesCsv(metrics.ReviewCandidates), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("ReviewCandidates.csv");

            // 6. ScanErrors.csv
            var errorsStaging = Path.Combine(stagingDir, "ScanErrors.csv");
            await File.WriteAllTextAsync(errorsStaging, GenerateErrorsCsv(errors), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("ScanErrors.csv");

            // 7. Inventory.csv (Top largest files summary)
            var inventoryStaging = Path.Combine(stagingDir, "Inventory.csv");
            await File.WriteAllTextAsync(inventoryStaging, GenerateInventoryCsv(metrics.LargestFiles), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("Inventory.csv");

            // 8. Manifest.json
            var manifest = new
            {
                ScanRunId = scanRunId,
                GeneratedAtUtc = DateTime.UtcNow,
                IsCompleteScan = run.Status == ScanState.Completed,
                TotalFiles = metrics.TotalFiles,
                TotalLogicalSizeBytes = metrics.TotalLogicalSizeBytes,
                TotalLogicalSizeMb = metrics.TotalLogicalSizeBytes / (1024.0 * 1024.0),
                ErrorCount = run.ErrorCount,
                ApplicationVersion = run.ApplicationVersion,
                GeneratedFiles = stagedFileNames.Concat(new[] { "Manifest.json" }).ToList()
            };

            var manifestStaging = Path.Combine(stagingDir, "Manifest.json");
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestStaging, manifestJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            stagedFileNames.Add("Manifest.json");

            // All files generated successfully in staging — now atomically commit to outputDir
            Directory.CreateDirectory(outputDir);
            var finalGeneratedFiles = new List<string>();

            foreach (var fileName in stagedFileNames)
            {
                var src = Path.Combine(stagingDir, fileName);
                var dest = Path.Combine(outputDir, fileName);
                File.Move(src, dest, overwrite: true);
                finalGeneratedFiles.Add(dest);
            }

            _logger.LogInformation("Successfully committed {Count} report files to {OutputDir}", finalGeneratedFiles.Count, outputDir);

            return new ReportGenerationResult
            {
                ScanRunId = scanRunId,
                OutputDirectory = outputDir,
                GeneratedAtUtc = DateTime.UtcNow,
                IsCompleteScan = run.Status == ScanState.Completed,
                GeneratedFiles = finalGeneratedFiles
            };
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up staging directory {StagingDir}", stagingDir);
                }
            }
        }
    }

    private static async Task WriteFileAtomicallyAsync(string finalPath, string content, CancellationToken cancellationToken)
    {
        var tempPath = finalPath + ".tmp_" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static string GenerateMarkdownSummary(ScanRun run, StorageMetricsSummary metrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LocalFileAudit — Storage Inventory Summary");
        sb.AppendLine();
        sb.AppendLine("> **Important Safety Notice**: LocalFileAudit is a decision-support and analysis tool. It strictly operates read-only and never modifies, moves, or deletes user files. Candidate classifications represent items worth reviewing, not files that are safe to delete.");
        sb.AppendLine();

        if (run.Status != ScanState.Completed)
        {
            sb.AppendLine($"> [!WARNING]");
            sb.AppendLine($"> **Incomplete Scan Warning**: This scan ended in state `{run.Status}`. Reported totals may not reflect the entire filesystem.");
            sb.AppendLine();
        }

        sb.AppendLine("## Scan Metadata");
        sb.AppendLine($"- **Scan Run ID**: `{run.Id}`");
        sb.AppendLine($"- **Start Time (UTC)**: {run.StartTimeUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Completion Status**: **{run.Status}**");
        sb.AppendLine($"- **Selected Roots**: `{run.SelectedDrives}`");
        sb.AppendLine($"- **Total Discovered Files**: {metrics.TotalFiles:N0}");
        sb.AppendLine($"- **Total Logical Size**: {metrics.TotalLogicalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB ({metrics.TotalLogicalSizeBytes:N0} bytes)");
        sb.AppendLine($"- **Errors Encountered**: {run.ErrorCount}");
        sb.AppendLine();

        sb.AppendLine("## Top Storage Consuming File Types");
        sb.AppendLine("| Extension | File Count | Total Size (MB) | % of Storage |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var ext in metrics.TopExtensions.Take(10))
        {
            sb.AppendLine($"| `{ext.Extension}` | {ext.FileCount:N0} | {ext.TotalLogicalSizeBytes / (1024.0 * 1024.0):F1} | {ext.PercentageOfTotalSize:F1}% |");
        }
        sb.AppendLine();

        sb.AppendLine("## Largest Individual Files");
        sb.AppendLine("| File Name | Extension | Size (MB) | Parent Directory |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var file in metrics.LargestFiles.Take(10))
        {
            sb.AppendLine($"| {EscapeMarkdown(file.FileName)} | `{file.Extension}` | {file.SizeBytes / (1024.0 * 1024.0):F1} | `{EscapeMarkdown(file.ParentPath)}` |");
        }
        sb.AppendLine();

        sb.AppendLine("## Top Candidates for Review");
        sb.AppendLine("| Category | File | Size (MB) | Rule ID | Explanation |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in metrics.ReviewCandidates.Take(15))
        {
            sb.AppendLine($"| **{c.Category}** | {EscapeMarkdown(c.FileName)} | {c.SizeBytes / (1024.0 * 1024.0):F1} | `{c.RuleId}` | {c.Explanation} |");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static string GenerateHtmlSummary(ScanRun run, StorageMetricsSummary metrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine("<title>LocalFileAudit Storage Summary</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #121316; color: #F3F4F6; margin: 0; padding: 30px; }
            .container { max-width: 1100px; margin: 0 auto; }
            h1 { font-size: 24px; margin-bottom: 6px; }
            .subtitle { color: #9CA3AF; font-size: 14px; margin-bottom: 24px; }
            .banner { background: #16382B; border: 1px solid #10B981; border-radius: 8px; padding: 16px; margin-bottom: 24px; color: #A7F3D0; }
            .warning-banner { background: #332414; border: 1px solid #F59E0B; border-radius: 8px; padding: 16px; margin-bottom: 24px; color: #FDE68A; }
            .metrics-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; margin-bottom: 24px; }
            .metric-card { background: #22242A; border: 1px solid #363943; border-radius: 8px; padding: 16px; }
            .metric-title { font-size: 11px; font-weight: bold; color: #9CA3AF; text-transform: uppercase; }
            .metric-value { font-size: 22px; font-weight: bold; margin-top: 6px; color: #F3F4F6; }
            table { width: 100%; border-collapse: collapse; margin-bottom: 30px; background: #22242A; border: 1px solid #363943; border-radius: 8px; overflow: hidden; }
            th { text-align: left; padding: 10px 14px; background: #2A2C34; color: #9CA3AF; font-size: 12px; font-weight: 600; }
            td { padding: 10px 14px; border-top: 1px solid #363943; font-size: 13px; color: #E5E7EB; word-break: break-all; }
            .badge { display: inline-block; padding: 3px 8px; border-radius: 4px; font-size: 11px; font-weight: 600; }
            .badge-archive { background: #1E3A8A; color: #93C5FD; }
            .badge-cleanup { background: #7C2D12; color: #FDBA74; }
            .badge-investigate { background: #78350F; color: #FDE68A; }
            .badge-protected { background: #064E3B; color: #6EE7B7; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        sb.AppendLine("<h1>LocalFileAudit — Storage Summary Report</h1>");
        sb.AppendLine($"<div class=\"subtitle\">Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | Scan Run: {run.Id}</div>");

        sb.AppendLine("<div class=\"banner\"><strong>✓ Safe Read-Only Analysis Guarantee</strong>: LocalFileAudit is a decision-support utility. It never modifies, moves, or deletes user files. All candidate classifications represent possibilities for user investigation.</div>");

        if (run.Status != ScanState.Completed)
        {
            sb.AppendLine($"<div class=\"warning-banner\"><strong>⚠ Incomplete Scan Notice</strong>: This scan ended with status <code>{WebUtility.HtmlEncode(run.Status.ToString())}</code>. Reported totals represent partial inventory.</div>");
        }

        sb.AppendLine("<div class=\"metrics-grid\">");
        sb.AppendLine($"<div class=\"metric-card\"><div class=\"metric-title\">Inventoried Files</div><div class=\"metric-value\">{metrics.TotalFiles:N0}</div></div>");
        sb.AppendLine($"<div class=\"metric-card\"><div class=\"metric-title\">Logical Storage</div><div class=\"metric-value\">{metrics.TotalLogicalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB</div></div>");
        sb.AppendLine($"<div class=\"metric-card\"><div class=\"metric-title\">Roots Scanned</div><div class=\"metric-value\">{WebUtility.HtmlEncode(run.SelectedDrives)}</div></div>");
        sb.AppendLine($"<div class=\"metric-card\"><div class=\"metric-title\">Scan Status</div><div class=\"metric-value\">{WebUtility.HtmlEncode(run.Status.ToString())}</div></div>");
        sb.AppendLine("</div>");

        // Top Extensions Table
        sb.AppendLine("<h2>Top Storage Consuming File Types</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Extension</th><th>File Count</th><th>Total Size (MB)</th><th>% of Total Storage</th></tr>");
        foreach (var ext in metrics.TopExtensions.Take(12))
        {
            sb.AppendLine($"<tr><td><code>{WebUtility.HtmlEncode(ext.Extension)}</code></td><td>{ext.FileCount:N0}</td><td>{ext.TotalLogicalSizeBytes / (1024.0 * 1024.0):F1} MB</td><td>{ext.PercentageOfTotalSize:F1}%</td></tr>");
        }
        sb.AppendLine("</table>");

        // Largest Files Table
        sb.AppendLine("<h2>Largest Files Detected</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>File Name</th><th>Extension</th><th>Size (MB)</th><th>Parent Directory</th></tr>");
        foreach (var f in metrics.LargestFiles.Take(10))
        {
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(f.FileName)}</td><td><code>{WebUtility.HtmlEncode(f.Extension)}</code></td><td>{f.SizeBytes / (1024.0 * 1024.0):F1} MB</td><td>{WebUtility.HtmlEncode(f.ParentPath)}</td></tr>");
        }
        sb.AppendLine("</table>");

        // Review Candidates Table
        sb.AppendLine("<h2>Top Candidates for Review</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Category</th><th>File Name</th><th>Size (MB)</th><th>Rule ID</th><th>Explanation</th></tr>");
        foreach (var c in metrics.ReviewCandidates.Take(15))
        {
            var badgeClass = c.Category switch
            {
                CandidateCategory.ArchiveCandidate => "badge-archive",
                CandidateCategory.CleanupCandidate => "badge-cleanup",
                CandidateCategory.Investigate => "badge-investigate",
                CandidateCategory.ProtectedSystem => "badge-protected",
                _ => "badge-investigate"
            };
            sb.AppendLine($"<tr><td><span class=\"badge {badgeClass}\">{WebUtility.HtmlEncode(c.Category.ToString())}</span></td><td>{WebUtility.HtmlEncode(c.FileName)}</td><td>{c.SizeBytes / (1024.0 * 1024.0):F1} MB</td><td><code>{WebUtility.HtmlEncode(c.RuleId)}</code></td><td>{WebUtility.HtmlEncode(c.Explanation)}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string GenerateExtensionCsv(IEnumerable<ExtensionSummary> list)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extension,FileCount,TotalSizeBytes,LogicalMB,PercentageOfStorage,PercentageOfFiles");
        foreach (var ext in list)
        {
            sb.AppendLine($"{EscapeCsv(ext.Extension)},{ext.FileCount},{ext.TotalLogicalSizeBytes},{(ext.TotalLogicalSizeBytes / (1024.0 * 1024.0)):F2},{ext.PercentageOfTotalSize:F2},{ext.PercentageOfTotalFiles:F2}");
        }
        return sb.ToString();
    }

    private static string GenerateDirectoryCsv(IEnumerable<DirectorySummary> list)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DirectoryPath,DirectSizeBytes,DirectLogicalMB,DirectFileCount");
        foreach (var dir in list)
        {
            sb.AppendLine($"{EscapeCsv(dir.DirectoryPath)},{dir.DirectSizeBytes},{(dir.DirectSizeBytes / (1024.0 * 1024.0)):F2},{dir.DirectFileCount}");
        }
        return sb.ToString();
    }

    private static string GenerateCandidatesCsv(IEnumerable<CandidateClassification> list)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FullPath,FileName,Extension,SizeBytes,LogicalMB,Category,RuleId,Explanation,LastWriteTimeUtc");
        foreach (var c in list)
        {
            sb.AppendLine($"{EscapeCsv(c.FullPath)},{EscapeCsv(c.FileName)},{EscapeCsv(c.Extension)},{c.SizeBytes},{(c.SizeBytes / (1024.0 * 1024.0)):F2},{c.Category},{EscapeCsv(c.RuleId)},{EscapeCsv(c.Explanation)},{c.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}");
        }
        return sb.ToString();
    }

    private static string GenerateErrorsCsv(IEnumerable<ScanErrorRecord> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,Operation,ErrorCode,ErrorMessage,TimestampUtc");
        foreach (var err in errors)
        {
            sb.AppendLine($"{EscapeCsv(err.Path)},{EscapeCsv(err.Operation)},{EscapeCsv(err.ErrorCode ?? "")},{EscapeCsv(err.ErrorMessage)},{err.TimestampUtc:yyyy-MM-dd HH:mm:ss}");
        }
        return sb.ToString();
    }

    private static string GenerateInventoryCsv(IEnumerable<FileRecord> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FullPath,FileName,Extension,SizeBytes,LogicalMB,CreationTimeUtc,LastWriteTimeUtc,Attributes");
        foreach (var f in files)
        {
            sb.AppendLine($"{EscapeCsv(f.FullPath)},{EscapeCsv(f.FileName)},{EscapeCsv(f.Extension)},{f.SizeBytes},{(f.SizeBytes / (1024.0 * 1024.0)):F2},{f.CreationTimeUtc:yyyy-MM-dd HH:mm:ss},{f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss},{f.Attributes}");
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string value) => CsvSanitizer.EscapeCsv(value);

    private static string EscapeMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("|", "\\|").Replace("*", "\\*").Replace("_", "\\_");
    }

    public Task<IReadOnlyList<ReportFolderInfo>> GetGeneratedReportsAsync(CancellationToken cancellationToken = default)
    {
        var reports = new List<ReportFolderInfo>();
        var reportsDir = _environment.ReportsDirectory;

        if (!Directory.Exists(reportsDir))
        {
            return Task.FromResult<IReadOnlyList<ReportFolderInfo>>(reports);
        }

        try
        {
            var directories = Directory.GetDirectories(reportsDir);
            foreach (var dir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirInfo = new DirectoryInfo(dir);
                var manifestPath = Path.Combine(dir, "Manifest.json");
                var htmlPath = Path.Combine(dir, "Summary.html");
                var mdPath = Path.Combine(dir, "Summary.md");

                Guid scanRunId = Guid.Empty;
                DateTime genTime = dirInfo.CreationTimeUtc;
                bool isComplete = false;
                long totalFiles = 0;
                double logicalMb = 0;
                long errorCount = 0;
                var reportFiles = Directory.GetFiles(dir).Select(Path.GetFileName).Where(f => f != null).Cast<string>().ToList();

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var json = File.ReadAllText(manifestPath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ScanRunId", out var runIdProp) && runIdProp.TryGetGuid(out var parsedGuid))
                        {
                            scanRunId = parsedGuid;
                        }
                        if (root.TryGetProperty("GeneratedAtUtc", out var genTimeProp) && genTimeProp.TryGetDateTime(out var parsedTime))
                        {
                            genTime = parsedTime;
                        }
                        if (root.TryGetProperty("IsCompleteScan", out var completeProp))
                        {
                            isComplete = completeProp.GetBoolean();
                        }
                        if (root.TryGetProperty("TotalFiles", out var filesProp))
                        {
                            totalFiles = filesProp.GetInt64();
                        }
                        if (root.TryGetProperty("TotalLogicalSizeMb", out var mbProp))
                        {
                            logicalMb = mbProp.GetDouble();
                        }
                        if (root.TryGetProperty("ErrorCount", out var errProp))
                        {
                            errorCount = errProp.GetInt64();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse Manifest.json in {ReportDirectory}", dir);
                    }
                }

                reports.Add(new ReportFolderInfo
                {
                    ScanRunId = scanRunId,
                    DirectoryPath = dir,
                    FolderName = dirInfo.Name,
                    GeneratedAtUtc = genTime,
                    IsCompleteScan = isComplete,
                    TotalFiles = totalFiles,
                    TotalLogicalSizeMb = logicalMb,
                    ErrorCount = errorCount,
                    ReportFiles = reportFiles,
                    HtmlSummaryPath = File.Exists(htmlPath) ? htmlPath : null,
                    MarkdownSummaryPath = File.Exists(mdPath) ? mdPath : null
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning generated reports in {ReportsDirectory}", reportsDir);
        }

        var sorted = reports.OrderByDescending(r => r.GeneratedAtUtc).ToList();
        return Task.FromResult<IReadOnlyList<ReportFolderInfo>>(sorted);
    }

    public async Task<long> ExportFullInventoryCsvAsync(
        Guid scanRunId,
        string destinationCsvPath,
        FileInventoryExportFilter? filter = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCsvPath);

        var run = await _repository.GetScanRunAsync(scanRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Scan run {scanRunId} not found.", nameof(scanRunId));

        var dir = Path.GetDirectoryName(destinationCsvPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".staging_inventory_{Guid.NewGuid():N}.tmp");
        long exportedCount = 0;
        long totalEvaluated = 0;

        try
        {
            var config = _configurationService?.Current ?? new Core.Configuration.AppConfiguration();
            var pageSize = 2000;
            var offset = 0;

            var totalFilesInScan = Math.Max(1, run.FilesDiscovered);

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                // Script matched headers: FullName, Extension, SizeBytes, LastWriteTime, AgeDays, RuleCategory
                await writer.WriteLineAsync("FullName,Extension,SizeBytes,LastWriteTime,AgeDays,RuleCategory");

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string? extFilter = null;
                    if (filter?.Extension != null && !string.IsNullOrWhiteSpace(filter.Extension) && filter.Extension != "All Extensions")
                    {
                        extFilter = filter.Extension;
                    }

                    var query = new FileInventoryQuery
                    {
                        ScanRunId = scanRunId,
                        Extension = extFilter,
                        MinSizeBytes = filter?.MinSizeBytes,
                        Offset = offset,
                        Limit = pageSize,
                        SortBy = "SizeBytes",
                        SortDescending = true
                    };

                    var page = await _repository.QueryFilesAsync(query, cancellationToken).ConfigureAwait(false);
                    if (page.Items.Count == 0)
                    {
                        break;
                    }

                    var now = DateTime.UtcNow;
                    foreach (var file in page.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        totalEvaluated++;

                        var classification = CandidateRuleEvaluator.Classify(file, config);
                        var isCandidate = classification != null &&
                            (classification.Category == CandidateCategory.ArchiveCandidate ||
                             classification.Category == CandidateCategory.CleanupCandidate ||
                             classification.Category == CandidateCategory.Investigate);

                        if (filter?.OnlyCandidates == true && !isCandidate)
                        {
                            continue;
                        }

                        var ruleCategory = classification?.Category.ToString() ?? "Keep";
                        var lastWriteTimeStr = file.LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
                        var ageDays = file.LastWriteTimeUtc.HasValue
                            ? (now - file.LastWriteTimeUtc.Value).TotalDays.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                            : "0.0";

                        var escapedPath = EscapeCsv(file.FullPath);
                        var escapedExtension = EscapeCsv(file.Extension);

                        var line = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0},{1},{2},{3},{4},{5}",
                            escapedPath,
                            escapedExtension,
                            file.SizeBytes,
                            lastWriteTimeStr,
                            ageDays,
                            ruleCategory);

                        await writer.WriteLineAsync(line);
                        exportedCount++;
                    }

                    offset += page.Items.Count;
                    var pct = Math.Min(100.0, (double)totalEvaluated / totalFilesInScan * 100.0);
                    progress?.Report(pct);

                    if (offset >= page.TotalCount)
                    {
                        break;
                    }
                }

                await writer.FlushAsync();
            }

            File.Move(tempPath, destinationCsvPath, overwrite: true);
            progress?.Report(100.0);
            _logger.LogInformation("Full inventory export completed: {ExportedCount} files exported to {Path}", exportedCount, destinationCsvPath);
            return exportedCount;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }
}
