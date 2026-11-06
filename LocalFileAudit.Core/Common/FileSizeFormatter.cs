using System;

namespace LocalFileAudit.Core.Common;

/// <summary>
/// Unified human-readable file and volume byte size formatting utility.
/// Standardizes byte representations (B, KB, MB, GB) across the entire application.
/// </summary>
public static class FileSizeFormatter
{
    private const double OneKb = 1024.0;
    private const double OneMb = 1024.0 * 1024.0;
    private const double OneGb = 1024.0 * 1024.0 * 1024.0;

    /// <summary>
    /// Formats a byte quantity into a human-readable string with appropriate binary magnitude suffix.
    /// Supports negative deltas cleanly.
    /// </summary>
    public static string Format(long bytes)
    {
        var abs = Math.Abs(bytes);
        if (abs >= 1024L * 1024L * 1024L)
            return $"{bytes / OneGb:F2} GB";
        if (abs >= 1024L * 1024L)
            return $"{bytes / OneMb:F2} MB";
        if (abs >= 1024L)
            return $"{bytes / OneKb:F1} KB";
        return $"{bytes:N0} B";
    }
}

