namespace LocalFileAudit.Core.Models;

/// <summary>
/// Durable record of an inventory scan run.
/// </summary>
public sealed class ScanRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndTimeUtc { get; set; }
    public ScanState Status { get; set; } = ScanState.Pending;
    public string SelectedDrives { get; set; } = string.Empty;
    public string ConfigurationSnapshot { get; set; } = string.Empty;
    public long FilesDiscovered { get; set; }
    public long DirectoriesCompleted { get; set; }
    public long ErrorCount { get; set; }
    public string ApplicationVersion { get; set; } = "1.0.0";
}

/// <summary>
/// Record representing an individual directory traversal.
/// </summary>
public sealed class DirectoryRecord
{
    public long Id { get; set; }
    public Guid ScanRunId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public DirectoryState Status { get; set; } = DirectoryState.Pending;
    public int FilesProcessed { get; set; }
    public DateTime LastCheckpointUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
}

/// <summary>
/// Record representing an individual discovered file.
/// </summary>
public sealed class FileRecord
{
    public long Id { get; set; }
    public Guid ScanRunId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? CreationTimeUtc { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public DateTime? LastAccessTimeUtc { get; set; }
    public int Attributes { get; set; }
    public bool IsHidden => (Attributes & (int)FileAttributes.Hidden) != 0;
    public bool IsReadOnly => (Attributes & (int)FileAttributes.ReadOnly) != 0;
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public ScanMetadataStatus MetadataStatus { get; set; } = ScanMetadataStatus.Ok;
    public string? HashAlgorithm { get; set; }
    public string? HashValue { get; set; }
}

/// <summary>
/// Record representing an error encountered during scanning.
/// </summary>
public sealed class ScanErrorRecord
{
    public long Id { get; set; }
    public Guid ScanRunId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
