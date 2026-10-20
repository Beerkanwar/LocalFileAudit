using System.Globalization;

namespace LocalFileAudit.Core.Common;

/// <summary>
/// Thread-safe helper to parse and format database ISO 8601 strings strictly as UTC DateTimes.
/// Prevents local time conversion, ensuring DateTimeKind is always Utc and avoiding timezone offset skew.
/// </summary>
public static class UtcDateTimeHelper
{
    private static readonly DateTimeStyles UtcStyles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

    public static DateTime ParseUtc(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return DateTime.Parse(value, CultureInfo.InvariantCulture, UtcStyles);
    }

    public static DateTime? ParseUtcNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ParseUtc(value);
    }

    public static string ToIsoString(DateTime dateTime)
    {
        var utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
        return utc.ToString("o", CultureInfo.InvariantCulture);
    }

    public static DateTime ToLocal(DateTime dateTime)
    {
        return dateTime.Kind == DateTimeKind.Utc
            ? dateTime.ToLocalTime()
            : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime();
    }

    public static DateTime? ToLocalNullable(DateTime? dateTime)
    {
        if (!dateTime.HasValue) return null;
        return ToLocal(dateTime.Value);
    }
}
