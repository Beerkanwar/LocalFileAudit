namespace LocalFileAudit.Core.Configuration;

/// <summary>
/// Root application configuration.
/// </summary>
public sealed class AppConfiguration
{
    public ScanningConfiguration Scanning { get; set; } = new();
    public AnalysisConfiguration Analysis { get; set; } = new();
    public AiConfiguration AI { get; set; } = new();
    public SafetyConfiguration Safety { get; set; } = new();
    public LoggingConfiguration Logging { get; set; } = new();
}

/// <summary>
/// Filesystem scanning options.
/// </summary>
public sealed class ScanningConfiguration
{
    public bool IncludeFixedDrivesByDefault { get; set; } = true;
    public bool FollowReparsePoints { get; set; } = false;
    public bool LowImpactMode { get; set; } = true;
    public int MaxConcurrentDirectoryWorkers { get; set; } = 2;
    public int DatabaseBatchSize { get; set; } = 500;
    public bool HashingEnabledByDefault { get; set; } = false;
}

/// <summary>
/// Deterministic storage analysis thresholds.
/// </summary>
public sealed class AnalysisConfiguration
{
    public long LargeFileThresholdBytes { get; set; } = 524_288_000; // 500 MB
    public int OldFileAgeDays { get; set; } = 730; // 2 years
    public bool ArchiveRecommendationsRequireReview { get; set; } = true;
}

/// <summary>
/// Local AI (Ollama) configuration.
/// </summary>
public sealed class AiConfiguration
{
    public bool Enabled { get; set; } = false;
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public bool RequireLocalEndpoint { get; set; } = true;
    public bool SendDocumentContents { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Safety configuration enforcing read-only constraints.
/// Automatic destructive operations are permanently disabled and unsupported in initial version.
/// </summary>
public sealed class SafetyConfiguration
{
    public bool AutomaticDeletionEnabled { get; set; } = false;
    public bool AutomaticMoveEnabled { get; set; } = false;
    public bool AutomaticCopyEnabled { get; set; } = false;
}

/// <summary>
/// Local logging settings.
/// </summary>
public sealed class LoggingConfiguration
{
    public string LogLevel { get; set; } = "Information";
    public bool RedactFilePaths { get; set; } = true;
    public int RetainDays { get; set; } = 14;
}
