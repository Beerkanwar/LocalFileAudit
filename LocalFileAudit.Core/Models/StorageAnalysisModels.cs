namespace LocalFileAudit.Core.Models;

/// <summary>
/// Aggregated storage statistics for a single file extension.
/// </summary>
public sealed class ExtensionSummary
{
    public string Extension { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalLogicalSizeBytes { get; set; }
    public double PercentageOfTotalSize { get; set; }
    public double PercentageOfTotalFiles { get; set; }
}

/// <summary>
/// Aggregated storage statistics for a directory.
/// </summary>
public sealed class DirectorySummary
{
    public string DirectoryPath { get; set; } = string.Empty;
    public long DirectSizeBytes { get; set; }
    public long DirectFileCount { get; set; }
    public long RecursiveSizeBytes { get; set; }
    public long RecursiveFileCount { get; set; }
    public bool IsComplete { get; set; } = true;
}

/// <summary>
/// Explainable deterministic candidate classification for storage decision support.
/// "CleanupCandidate" means "worth investigating", never "safe to delete".
/// </summary>
public sealed class CandidateClassification
{
    public long FileRecordId { get; set; }
    public Guid ScanRunId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public CandidateCategory Category { get; set; } = CandidateCategory.Unknown;
    public string RuleId { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public int Priority { get; set; } = 3; // 1 = High, 2 = Medium, 3 = Low
    public double Confidence { get; set; } = 1.0;
}

/// <summary>
/// Full metrics summary produced by deterministic storage analysis.
/// </summary>
public sealed class StorageMetricsSummary
{
    public Guid ScanRunId { get; set; }
    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
    public long TotalFiles { get; set; }
    public long TotalLogicalSizeBytes { get; set; }
    public long ErrorCount { get; set; }
    public bool IsScanComplete { get; set; }
    public long CloudPlaceholderCount { get; set; }
    public long CloudPlaceholderSizeBytes { get; set; }
    public long VolumeCapacityBytes { get; set; }
    public long VolumeFreeBytes { get; set; }
    public long VolumeUsedBytes => Math.Max(0, VolumeCapacityBytes - VolumeFreeBytes);
    public double VolumeUsagePercentage => VolumeCapacityBytes > 0 ? (double)VolumeUsedBytes / VolumeCapacityBytes * 100.0 : 0.0;
    public string SelectedDrives { get; set; } = string.Empty;

    public List<ExtensionSummary> TopExtensions { get; set; } = new();
    public List<FileRecord> LargestFiles { get; set; } = new();
    public List<DirectorySummary> LargestDirectories { get; set; } = new();
    public List<CandidateClassification> ReviewCandidates { get; set; } = new();
}

/// <summary>
/// Result metadata of report generation.
/// </summary>
public sealed class ReportGenerationResult
{
    public Guid ScanRunId { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsCompleteScan { get; set; }
    public List<string> GeneratedFiles { get; set; } = new();
}

/// <summary>
/// Partitions directory trees into non-overlapping sets suitable for squarified treemaps and storage hotspots.
/// </summary>
public static class TreemapPartitionHelper
{
    public static IReadOnlyList<DirectorySummary> FilterNonOverlappingDirectories(IEnumerable<DirectorySummary>? directories)
    {
        if (directories == null) return Array.Empty<DirectorySummary>();

        var candidates = directories
            .Where(d => d.RecursiveSizeBytes > 0 || d.DirectSizeBytes > 0)
            .OrderByDescending(d => d.RecursiveSizeBytes > 0 ? d.RecursiveSizeBytes : d.DirectSizeBytes)
            .ToList();

        var normalized = candidates
            .Select(d => d.DirectoryPath.TrimEnd('\\', '/'))
            .ToList();

        var result = new List<DirectorySummary>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var path = normalized[i];
            var prefix = path + "\\";
            bool hasDescendant = false;

            for (int j = 0; j < candidates.Count; j++)
            {
                if (i == j) continue;
                if (normalized[j].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hasDescendant = true;
                    break;
                }
            }

            if (!hasDescendant)
            {
                result.Add(candidates[i]);
            }
        }

        return result;
    }
}
