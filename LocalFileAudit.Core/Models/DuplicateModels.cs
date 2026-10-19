namespace LocalFileAudit.Core.Models;

/// <summary>
/// Progress reporting for asynchronous SHA-256 duplicate hashing jobs.
/// </summary>
public sealed class HashJobProgress
{
    public int FilesProcessed { get; init; }
    public int TotalFilesToHash { get; init; }
    public long BytesHashed { get; init; }
    public string CurrentFilePath { get; init; } = string.Empty;
    public double PercentComplete => TotalFilesToHash > 0 ? (double)FilesProcessed / TotalFilesToHash * 100.0 : 0.0;
}

/// <summary>
/// Represents a cluster of files that share the exact same byte size,
/// and optionally matching cryptographic SHA-256 hashes.
/// </summary>
public sealed class DuplicateGroup
{
    public long SizeBytes { get; init; }
    public string? HashValue { get; set; }
    public bool IsVerifiedByHash => !string.IsNullOrEmpty(HashValue);
    public List<FileRecord> Files { get; init; } = new();

    /// <summary>
    /// Theoretical storage wasted by duplicate copies beyond the first instance.
    /// </summary>
    public long WastedSizeBytes => Files.Count > 1 ? (Files.Count - 1) * SizeBytes : 0;
    public double WastedSizeMb => Math.Round(WastedSizeBytes / (1024.0 * 1024.0), 2);
    public double SingleFileMb => Math.Round(SizeBytes / (1024.0 * 1024.0), 2);
}

/// <summary>
/// Comprehensive summary of a two-stage duplicate detection run.
/// </summary>
public sealed class DuplicateAnalysisSummary
{
    public Guid ScanRunId { get; init; }
    public int PotentialGroupsCount { get; init; }
    public int VerifiedGroupsCount { get; init; }
    public int TotalDuplicateFilesCount { get; init; }
    public long PotentialWastedSizeBytes { get; init; }
    public long VerifiedWastedSizeBytes { get; init; }
    public double PotentialWastedSizeGb => Math.Round(PotentialWastedSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
    public double VerifiedWastedSizeGb => Math.Round(VerifiedWastedSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
    public List<DuplicateGroup> Groups { get; init; } = new();
}
