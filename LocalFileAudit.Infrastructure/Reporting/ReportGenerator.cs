using System.Net;
using System.Text;
using System.Text.Json;
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

    public ReportGenerator(
        IStorageAnalysisEngine analysisEngine,
        IScanRepository repository,
        IAppEnvironment environment,
        ILogger<ReportGenerator> logger)
    {
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        Directory.CreateDirectory(outputDir);
        _logger.LogInformation("Generating reports for scan {ScanRunId} into {OutputDir}", scanRunId, outputDir);

        var generatedFiles = new List<string>();

        // 1. Summary.md
        var mdPath = Path.Combine(outputDir, "Summary.md");
        await WriteFileAtomicallyAsync(mdPath, GenerateMarkdownSummary(run, metrics), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(mdPath);

        // 2. Summary.html (Secure, local-only, escaped HTML)
        var htmlPath = Path.Combine(outputDir, "Summary.html");
        await WriteFileAtomicallyAsync(htmlPath, GenerateHtmlSummary(run, metrics), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(htmlPath);

        // 3. ExtensionSummary.csv
        var extCsvPath = Path.Combine(outputDir, "ExtensionSummary.csv");
        await WriteFileAtomicallyAsync(extCsvPath, GenerateExtensionCsv(metrics.TopExtensions), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(extCsvPath);

        // 4. DirectorySummary.csv
        var dirCsvPath = Path.Combine(outputDir, "DirectorySummary.csv");
        await WriteFileAtomicallyAsync(dirCsvPath, GenerateDirectoryCsv(metrics.LargestDirectories), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(dirCsvPath);

        // 5. ReviewCandidates.csv
        var candidatesCsvPath = Path.Combine(outputDir, "ReviewCandidates.csv");
        await WriteFileAtomicallyAsync(candidatesCsvPath, GenerateCandidatesCsv(metrics.ReviewCandidates), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(candidatesCsvPath);

        // 6. ScanErrors.csv
        var errorsCsvPath = Path.Combine(outputDir, "ScanErrors.csv");
        await WriteFileAtomicallyAsync(errorsCsvPath, GenerateErrorsCsv(errors), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(errorsCsvPath);

        // 7. Inventory.csv (Top largest files summary)
        var inventoryCsvPath = Path.Combine(outputDir, "Inventory.csv");
        await WriteFileAtomicallyAsync(inventoryCsvPath, GenerateInventoryCsv(metrics.LargestFiles), cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(inventoryCsvPath);

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
            GeneratedFiles = generatedFiles.Select(Path.GetFileName).ToList()
        };

        var manifestPath = Path.Combine(outputDir, "Manifest.json");
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await WriteFileAtomicallyAsync(manifestPath, manifestJson, cancellationToken).ConfigureAwait(false);
        generatedFiles.Add(manifestPath);

        _logger.LogInformation("Successfully generated {Count} report files.", generatedFiles.Count);

        return new ReportGenerationResult
        {
            ScanRunId = scanRunId,
            OutputDirectory = outputDir,
            GeneratedAtUtc = DateTime.UtcNow,
            IsCompleteScan = run.Status == ScanState.Completed,
            GeneratedFiles = generatedFiles
        };
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

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

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
}
