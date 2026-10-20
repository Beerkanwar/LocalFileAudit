namespace LocalFileAudit.Core.Models;

/// <summary>
/// Status of an overall scan run.
/// </summary>
public enum ScanState
{
    Pending,
    InProgress,
    Paused,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Status of an individual directory traversal.
/// </summary>
public enum DirectoryState
{
    Pending,
    InProgress,
    Completed,
    Skipped,
    Error,
    Cancelled
}

/// <summary>
/// Deterministic candidate classification categories.
/// "CleanupCandidate" means "worth investigating", never "safe to delete".
/// </summary>
public enum CandidateCategory
{
    Unknown,
    KeepOnLaptop,
    ArchiveCandidate,
    Investigate,
    CleanupCandidate,
    ProtectedSystem
}

/// <summary>
/// Status of file metadata reading during scanning.
/// </summary>
public enum ScanMetadataStatus
{
    Ok,
    Inaccessible,
    NotFound,
    SharingViolation,
    PathTooLong,
    CloudPlaceholder,
    Error
}
