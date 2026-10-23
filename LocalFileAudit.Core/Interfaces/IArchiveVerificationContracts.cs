using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LocalFileAudit.Core.Interfaces;

public enum VerificationStatus
{
    Match,
    MissingAtDestination,
    SizeMismatch,
    HashMismatch,
    ExtraAtDestination,
    HashError
}

public sealed class ArchiveVerificationItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string SourceFullPath { get; set; } = string.Empty;
    public string DestinationFullPath { get; set; } = string.Empty;
    public long SourceSizeBytes { get; set; }
    public long DestinationSizeBytes { get; set; }
    public string? SourceHash { get; set; }
    public string? DestinationHash { get; set; }
    public VerificationStatus Status { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class ArchiveVerificationSummary
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public DateTime VerifiedAtUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public bool Sha256Calculated { get; set; }
    public int TotalSourceFiles { get; set; }
    public long TotalSourceSizeBytes { get; set; }
    public int TotalDestinationFiles { get; set; }
    public long TotalDestinationSizeBytes { get; set; }

    public int MatchCount { get; set; }
    public int MissingCount { get; set; }
    public int SizeMismatchCount { get; set; }
    public int HashMismatchCount { get; set; }
    public int HashErrorCount { get; set; }
    public int ExtraCount { get; set; }

    /// <summary>
    /// Indicates whether any directories or files were inaccessible or skipped during enumeration.
    /// If true, verification is partial and source files must NOT be deleted.
    /// </summary>
    public bool IsPartial { get; set; }
    public List<string> EnumerationErrors { get; set; } = new();

    public List<ArchiveVerificationItem> Items { get; set; } = new();
}

public interface IArchiveVerificationService
{
    Task<ArchiveVerificationSummary> VerifyArchiveAsync(
        string sourceDirectory,
        string destinationDirectory,
        bool calculateSha256 = false,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> ExportVerificationCsvAsync(
        ArchiveVerificationSummary summary,
        string? destinationCsvPath = null,
        CancellationToken cancellationToken = default);
}
