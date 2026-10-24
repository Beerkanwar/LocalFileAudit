namespace LocalFileAudit.Core.Models;

/// <summary>
/// Filter criteria for full file inventory CSV exports.
/// </summary>
public sealed class FileInventoryExportFilter
{
    public string? Extension { get; set; }
    public long? MinSizeBytes { get; set; }
    public string? SearchText { get; set; }
    public DateTime? MinModifiedUtc { get; set; }
    public DateTime? MaxModifiedUtc { get; set; }
    public bool OnlyCandidates { get; set; }
}
