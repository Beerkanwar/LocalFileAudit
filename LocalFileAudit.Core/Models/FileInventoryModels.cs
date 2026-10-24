namespace LocalFileAudit.Core.Models;

/// <summary>
/// Query criteria for searching, filtering, and paginating the SQLite file inventory.
/// </summary>
public sealed class FileInventoryQuery
{
    public Guid? ScanRunId { get; set; }
    public string? SearchText { get; set; }
    public string? Extension { get; set; }
    public string? DriveOrPrefix { get; set; }
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public DateTime? MinModifiedUtc { get; set; }
    public DateTime? MaxModifiedUtc { get; set; }
    public string SortBy { get; set; } = "SizeBytes"; // SizeBytes, FileName, LastWriteTimeUtc, Extension, FullPath
    public bool SortDescending { get; set; } = true;
    public int Offset { get; set; } = 0;
    public int Limit { get; set; } = 100;
    public long? AfterId { get; set; }
}

/// <summary>
/// Generic paginated result container.
/// </summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public long TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

/// <summary>
/// Metadata describing a previously generated audit report folder.
/// </summary>
public sealed class ReportFolderInfo
{
    public Guid ScanRunId { get; init; }
    public string DirectoryPath { get; init; } = string.Empty;
    public string FolderName { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public bool IsCompleteScan { get; init; }
    public long TotalFiles { get; init; }
    public double TotalLogicalSizeMb { get; init; }
    public long ErrorCount { get; init; }
    public IReadOnlyList<string> ReportFiles { get; init; } = Array.Empty<string>();
    public string? HtmlSummaryPath { get; init; }
    public string? MarkdownSummaryPath { get; init; }
}
