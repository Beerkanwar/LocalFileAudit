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
        // Exempt valid numeric values (+123, -45.67) to prevent breaking numeric interpretation in spreadsheets
        if (DangerousLeadingChars.Contains(sanitized[0]))
        {
            var isNumeric = (sanitized[0] == '+' || sanitized[0] == '-') &&
                            double.TryParse(sanitized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

            if (!isNumeric)
            {
                sanitized = "'" + sanitized;
            }
        }

        // Standard RFC 4180 quote escaping
        if (sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n') || sanitized.Contains('\r') || sanitized.Contains(';'))
        {
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";
        }

        return sanitized;
    }
}
