namespace LocalFileAudit.Core.Models;

/// <summary>
/// Status of the connection to local Ollama API.
/// </summary>
public sealed class AiConnectionStatus
{
    public bool IsAvailable { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public IReadOnlyList<string> AvailableModels { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
    public long ResponseTimeMs { get; init; }
}

/// <summary>
/// State of an AI analysis job.
/// </summary>
public enum AiReportState
{
    Pending,
    Generating,
    Completed,
    Failed
}

/// <summary>
/// Compact candidate representation for data minimization in AI prompts.
/// Full file contents are NEVER included.
/// </summary>
public sealed class AiCandidateMetadata
{
    public long Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public double SizeMb { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public string DeterministicCategory { get; init; } = string.Empty;
    public string ParentDirectoryName { get; init; } = string.Empty;
    public DateTime? LastModifiedUtc { get; init; }
}

/// <summary>
/// Single decision-support recommendation produced by local AI.
/// </summary>
public sealed class AiRecommendation
{
    public long CandidateId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string SuggestedCategory { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string ConfidenceLevel { get; init; } = "Medium";
    public string Uncertainty { get; init; } = string.Empty;
    public string RecommendedVerificationStep { get; init; } = string.Empty;
}

/// <summary>
/// Validated structured response from local AI inference.
/// </summary>
public sealed class AiAnalysisResponse
{
    public string ExecutiveSummary { get; init; } = string.Empty;
    public string StorageHotspotsAssessment { get; init; } = string.Empty;
    public IReadOnlyList<AiRecommendation> Recommendations { get; init; } = Array.Empty<AiRecommendation>();
    public IReadOnlyList<string> PrioritizedReviewPlan { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SafetyWarnings { get; init; } = Array.Empty<string>();
    public string RawJson { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Durable database record of an AI report execution.
/// </summary>
public sealed class AiReportRecord
{
    public long Id { get; set; }
    public Guid ScanRunId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = "1.0";
    public AiReportState Status { get; set; } = AiReportState.Pending;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ReportPath { get; set; }
    public string? ErrorMessage { get; set; }
}
