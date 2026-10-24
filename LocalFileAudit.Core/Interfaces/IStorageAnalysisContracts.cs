using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Deterministic storage analysis engine that runs 100% locally without AI.
/// </summary>
public interface IStorageAnalysisEngine
{
    Task<StorageMetricsSummary> AnalyzeScanAsync(Guid scanRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExtensionSummary>> GetExtensionStatisticsAsync(Guid scanRunId, int top = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileRecord>> GetLargestFilesAsync(Guid scanRunId, int top = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectorySummary>> GetLargestDirectoriesAsync(Guid scanRunId, int top = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidateClassification>> ClassifyCandidatesAsync(Guid scanRunId, CancellationToken cancellationToken = default);
    Task InvalidateCacheAsync(Guid? scanRunId = null, CancellationToken cancellationToken = default);
    void InvalidateCache(Guid? scanRunId = null);
}

/// <summary>
/// Generates deterministic audit reports (Markdown, HTML, CSV, JSON) atomically.
/// </summary>
public interface IReportGenerator
{
    Task<ReportGenerationResult> GenerateReportsAsync(Guid scanRunId, string? targetDirectory = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportFolderInfo>> GetGeneratedReportsAsync(CancellationToken cancellationToken = default);
    Task<long> ExportFullInventoryCsvAsync(Guid scanRunId, string destinationCsvPath, FileInventoryExportFilter? filter = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
