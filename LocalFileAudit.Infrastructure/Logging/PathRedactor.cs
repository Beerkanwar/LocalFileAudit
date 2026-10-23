using System.IO;
using System.Text.RegularExpressions;

namespace LocalFileAudit.Infrastructure.Logging;

/// <summary>
/// Redacts personal Windows user paths to prevent accidental exposure of personal usernames or directory structures in logs and AI inference.
/// </summary>
public static partial class PathRedactor
{
    private static readonly Lazy<string?> CurrentUserProfile = new(() =>
    {
        try
        {
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return null;
        }
    });

    [GeneratedRegex(@"[a-zA-Z]:[/\\]Users[/\\][^/\\\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DriveUserProfilePattern();

    [GeneratedRegex(@"\\\\[^/\\\r\n]+[/\\][^/\\\r\n]+[/\\]Users[/\\][^/\\\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UncUserProfilePattern();

    [GeneratedRegex(@"(?:/home/|/Users/)[^/\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PosixUserProfilePattern();

    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var profile = CurrentUserProfile.Value;
        if (!string.IsNullOrEmpty(profile) && message.Contains(profile, StringComparison.OrdinalIgnoreCase))
        {
            message = message.Replace(profile, @"C:\Users\[USER]", StringComparison.OrdinalIgnoreCase);
        }

        message = DriveUserProfilePattern().Replace(message, @"C:\Users\[USER]");
        message = UncUserProfilePattern().Replace(message, @"\\SERVER\SHARE\Users\[USER]");
        message = PosixUserProfilePattern().Replace(message, @"/home/[USER]");

        return message;
    }

    /// <summary>
    /// Redacts a file name to avoid leaking confidential or personal names while retaining the file extension for classification.
    /// </summary>
    public static string RedactFileName(string? fileName, long? candidateId = null)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        string ext = Path.GetExtension(fileName);
        return candidateId.HasValue
            ? $"candidate_{candidateId.Value}{ext}"
            : $"file_[REDACTED]{ext}";
    }
}

