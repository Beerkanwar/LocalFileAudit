namespace LocalFileAudit.Core.Models;

/// <summary>
/// Real-time progress snapshot during scanning.
/// </summary>
public sealed class ScanProgress
{
    public Guid ScanRunId { get; set; }
    public string CurrentDirectory { get; set; } = string.Empty;
    public long FilesDiscovered { get; set; }
    public long DirectoriesCompleted { get; set; }
    public long FilesProcessed { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public double FilesPerSecond { get; set; }
    public long ErrorCount { get; set; }
    public ScanState State { get; set; } = ScanState.InProgress;
    public string? StatusDetail { get; set; }
}

/// <summary>
/// Scope definition for an inventory scan.
/// </summary>
public sealed class ScanTarget
{
    public List<string> TargetPaths { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    public bool IncludeFixedDrivesByDefault { get; set; } = true;
    public bool FollowReparsePoints { get; set; } = false;
    public bool LowImpactMode { get; set; } = true;
    public bool RecordCloudPlaceholders { get; set; } = true;
}
