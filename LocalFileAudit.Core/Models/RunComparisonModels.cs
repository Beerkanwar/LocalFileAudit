namespace LocalFileAudit.Core.Models;

public enum DeltaType
{
    New,
    Deleted,
    Modified,
    Unchanged
}

public sealed class FileDeltaRecord
{
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public DeltaType DeltaType { get; set; }
    public long BaselineSizeBytes { get; set; }
    public long TargetSizeBytes { get; set; }
    public long SizeDeltaBytes => TargetSizeBytes - BaselineSizeBytes;
    public DateTime? BaselineModifiedUtc { get; set; }
    public DateTime? TargetModifiedUtc { get; set; }
}

public sealed class ExtensionDelta
{
    public string Extension { get; set; } = string.Empty;
    public int NewCount { get; set; }
    public int DeletedCount { get; set; }
    public int ModifiedCount { get; set; }
    public long BaselineTotalBytes { get; set; }
    public long TargetTotalBytes { get; set; }
    public long NetDeltaBytes => TargetTotalBytes - BaselineTotalBytes;
}

public sealed class RunComparisonResult
{
    public Guid BaselineRunId { get; set; }
    public Guid TargetRunId { get; set; }
    public DateTime BaselineTimeUtc { get; set; }
    public DateTime TargetTimeUtc { get; set; }
    public int NewFilesCount { get; set; }
    public int DeletedFilesCount { get; set; }
    public int ModifiedFilesCount { get; set; }
    public long NewFilesBytes { get; set; }
    public long DeletedFilesBytes { get; set; }
    public long NetStorageDeltaBytes { get; set; }
    public List<FileDeltaRecord> Deltas { get; set; } = new();
    public List<ExtensionDelta> ExtensionDeltas { get; set; } = new();
}
