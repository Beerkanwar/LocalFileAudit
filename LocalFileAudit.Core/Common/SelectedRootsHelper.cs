using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalFileAudit.Core.Common;

/// <summary>
/// Helper to serialize and parse scanned target roots safely.
/// In Windows filesystems, ';' and ',' are legal folder characters, whereas '|' is strictly illegal.
/// Storing with '|' prevents paths with semicolons or commas from being erroneously fragmented,
/// while maintaining backward compatibility for existing historical scans stored with ';' or ','.
/// </summary>
public static class SelectedRootsHelper
{
    public const char Separator = '|';

    public static string JoinRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return string.Join(Separator.ToString(), roots.Select(r => r.Trim()).Where(r => !string.IsNullOrEmpty(r)));
    }

    public static IReadOnlyList<string> ParseRoots(string? selectedDrives)
    {
        if (string.IsNullOrWhiteSpace(selectedDrives))
        {
            return Array.Empty<string>();
        }

        if (selectedDrives.Contains(Separator))
        {
            return selectedDrives
                .Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => !string.IsNullOrEmpty(r))
                .ToList();
        }

        // Backward compatibility fallback for historical scans stored with ';' or ','
        return selectedDrives
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .Where(r => !string.IsNullOrEmpty(r))
            .ToList();
    }
}
