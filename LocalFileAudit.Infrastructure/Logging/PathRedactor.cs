using System.Text.RegularExpressions;

namespace LocalFileAudit.Infrastructure.Logging;

/// <summary>
/// Redacts personal Windows user paths to prevent accidental exposure of personal usernames or directory structures in logs.
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

    [GeneratedRegex(@"[a-zA-Z]:\\Users\\[^\\]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UserProfilePattern();

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

        return UserProfilePattern().Replace(message, @"C:\Users\[USER]");
    }
}
