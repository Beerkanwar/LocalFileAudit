using System;
using System.Collections.Generic;

namespace LocalFileAudit.Core.Models;

public sealed class ArchiveBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BatchName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string PlannedDestinationPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public long EstimatedSizeBytes { get; set; }
    public int EstimatedFileCount { get; set; }
    public bool IsVerified { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAtUtc { get; set; }
    public List<string> CandidatePaths { get; set; } = new();
}

public sealed class ArchivePlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlanName { get; set; } = "Local Storage Archive Plan";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedUtc { get; set; }
    public List<ArchiveBatch> Batches { get; set; } = new();
}

public sealed class ArchiveScriptGenerationResult
{
    public string ManifestCsvPath { get; set; } = string.Empty;
    public string PreviewScriptPath { get; set; } = string.Empty;
    public string CopyScriptPath { get; set; } = string.Empty;
}
