using System.IO;

namespace LocalFileAudit.Core.Common;

/// <summary>
/// Validates that source and destination paths do not overlap or nest within each other.
/// Canonicalizes paths using Path.GetFullPath to prevent traversal escapes (..),
/// relative paths, trailing slash mismatches, and drive aliases.
/// </summary>
public static class PathOverlapValidator
{
    public static void ValidateDoNotOverlap(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string srcFull;
        string dstFull;
        try
        {
            srcFull = Path.GetFullPath(source.Trim());
            dstFull = Path.GetFullPath(destination.Trim());
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid path format: {ex.Message}", ex);
        }

        var sep = Path.DirectorySeparatorChar;
        var altSep = Path.AltDirectorySeparatorChar;

        var srcNorm = srcFull.TrimEnd(sep, altSep) + sep;
        var dstNorm = dstFull.TrimEnd(sep, altSep) + sep;

        if (string.Equals(srcNorm, dstNorm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and Destination cannot be the exact same directory.");
        }

        if (dstNorm.StartsWith(srcNorm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Destination directory cannot be nested inside the source directory.");
        }

        if (srcNorm.StartsWith(dstNorm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source directory cannot be nested inside the destination directory.");
        }
    }
}
