using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Logging;

/// <summary>
/// Configuration options for the local rolling file logger.
/// </summary>
public sealed class FileLoggerOptions
{
    public string? LogDirectory { get; set; }
    public string LogFileNamePrefix { get; set; } = "localfileaudit";
    public int RetainDays { get; set; } = 14;
    public bool RedactFilePaths { get; set; } = true;
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
}
