using System;
using System.Linq;

namespace LocalFileAudit.Core.Common;

/// <summary>
/// Hardened RFC 4180 CSV escaping with formula injection defense.
/// Prevents DDE and spreadsheet formula execution (=, +, -, @, \t, \r) in Excel, Calc, etc.
/// </summary>
public static class CsvSanitizer
{
    private static readonly char[] DangerousLeadingChars = { '=', '+', '-', '@', '\t', '\r' };

    public static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sanitized = value;

        // Formula injection protection: if string starts with formula command characters, prepend an apostrophe
        if (DangerousLeadingChars.Contains(sanitized[0]))
        {
            sanitized = "'" + sanitized;
        }

        // Standard RFC 4180 quote escaping
        if (sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n') || sanitized.Contains('\r') || sanitized.Contains(';'))
        {
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";
        }

        return sanitized;
    }
}
