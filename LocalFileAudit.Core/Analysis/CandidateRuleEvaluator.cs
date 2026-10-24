using System.Text.RegularExpressions;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Analysis;

/// <summary>
/// Deterministic, explainable rule evaluator.
/// Classifies files into decision-support categories without automatic mutation.
/// </summary>
public static partial class CandidateRuleEvaluator
{
    private static readonly HashSet<string> MediaAndArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".vhd", ".vhdx",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v"
    };

    private static readonly HashSet<string> TempAndBackupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".bak", ".old", ".dmp", ".log", ".chk", ".swp", ".~tmp", ".crdownload"
    };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".exe", ".pkg"
    };

    public static readonly HashSet<string> AllCandidateExtensions = new(
        MediaAndArchiveExtensions.Concat(TempAndBackupExtensions).Concat(InstallerExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static CandidateClassification Classify(FileRecord file, AppConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(config);

        var path = file.FullPath;
        var ext = file.Extension;
        var now = DateTime.UtcNow;

        // 1. Protected System Directory Rule
        if (IsProtectedPath(path))
        {
            return new CandidateClassification
            {
                FileRecordId = file.Id,
                ScanRunId = file.ScanRunId,
                FullPath = file.FullPath,
                FileName = file.FileName,
                Extension = file.Extension,
                SizeBytes = file.SizeBytes,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                Category = CandidateCategory.ProtectedSystem,
                RuleId = "RULE_PROTECTED_SYSTEM",
                Priority = 3,
                Confidence = 1.0,
                Explanation = "Operating system or application system file. Protected location."
            };
        }

        // 2. Large File Threshold Rule
        if (file.SizeBytes >= config.Analysis.LargeFileThresholdBytes)
        {
            var mb = file.SizeBytes / (1024 * 1024);
            var thresholdMb = config.Analysis.LargeFileThresholdBytes / (1024 * 1024);
            return new CandidateClassification
            {
                FileRecordId = file.Id,
                ScanRunId = file.ScanRunId,
                FullPath = file.FullPath,
                FileName = file.FileName,
                Extension = file.Extension,
                SizeBytes = file.SizeBytes,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                Category = CandidateCategory.Investigate,
                RuleId = "RULE_LARGE_FILE",
                Priority = 1,
                Confidence = 0.95,
                Explanation = $"Large file ({mb:N0} MB) exceeding threshold ({thresholdMb:N0} MB). Review for retention or cold archive."
            };
        }

        // 3. Old Media or Archive File Rule
        if (MediaAndArchiveExtensions.Contains(ext) && file.LastWriteTimeUtc.HasValue)
        {
            var ageDays = (now - file.LastWriteTimeUtc.Value).TotalDays;
            if (ageDays >= config.Analysis.OldFileAgeDays)
            {
                return new CandidateClassification
                {
                    FileRecordId = file.Id,
                    ScanRunId = file.ScanRunId,
                    FullPath = file.FullPath,
                    FileName = file.FileName,
                    Extension = file.Extension,
                    SizeBytes = file.SizeBytes,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    Category = CandidateCategory.ArchiveCandidate,
                    RuleId = "RULE_OLD_ARCHIVE_MEDIA",
                    Priority = 1,
                    Confidence = 0.90,
                    Explanation = $"Archive/media file inactive for {(int)ageDays:N0} days (threshold: {config.Analysis.OldFileAgeDays} days). Candidate for external USB/cold storage."
                };
            }
        }

        // 4. Inactive Temporary or Backup Artifact Rule
        if (TempAndBackupExtensions.Contains(ext) && file.LastWriteTimeUtc.HasValue)
        {
            var ageDays = (now - file.LastWriteTimeUtc.Value).TotalDays;
            if (ageDays >= 180)
            {
                return new CandidateClassification
                {
                    FileRecordId = file.Id,
                    ScanRunId = file.ScanRunId,
                    FullPath = file.FullPath,
                    FileName = file.FileName,
                    Extension = file.Extension,
                    SizeBytes = file.SizeBytes,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    Category = CandidateCategory.CleanupCandidate,
                    RuleId = "RULE_INACTIVE_BACKUP_TEMP",
                    Priority = 2,
                    Confidence = 0.85,
                    Explanation = $"Backup or temporary extension inactive for {(int)ageDays:N0} days. Worth investigating whether source tool still needs it. Never delete automatically."
                };
            }
        }

        // 5. Inactive Installer in Downloads Rule
        if (InstallerExtensions.Contains(ext) && IsUserDownloadsPath(path) && file.LastWriteTimeUtc.HasValue)
        {
            var ageDays = (now - file.LastWriteTimeUtc.Value).TotalDays;
            if (ageDays >= 90)
            {
                return new CandidateClassification
                {
                    FileRecordId = file.Id,
                    ScanRunId = file.ScanRunId,
                    FullPath = file.FullPath,
                    FileName = file.FileName,
                    Extension = file.Extension,
                    SizeBytes = file.SizeBytes,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    Category = CandidateCategory.CleanupCandidate,
                    RuleId = "RULE_OLD_DOWNLOADED_INSTALLER",
                    Priority = 2,
                    Confidence = 0.85,
                    Explanation = $"Installer in Downloads folder inactive for {(int)ageDays:N0} days. Review whether installer can be archived or cleaned."
                };
            }
        }

        // Default classification: Keep On Laptop
        return new CandidateClassification
        {
            FileRecordId = file.Id,
            ScanRunId = file.ScanRunId,
            FullPath = file.FullPath,
            FileName = file.FileName,
            Extension = file.Extension,
            SizeBytes = file.SizeBytes,
            LastWriteTimeUtc = file.LastWriteTimeUtc,
            Category = CandidateCategory.KeepOnLaptop,
            RuleId = "RULE_DEFAULT_KEEP",
            Priority = 3,
            Confidence = 0.80,
            Explanation = "Normal active file within standard age and size thresholds."
        };
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:|\\\\[^\\]+\\[^\\]+)?\\Users\\[^\\]+\\Downloads(?:\\[^\r\n]*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UserDownloadsPattern();

    public static bool IsProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('/', '\\');

        // Check well-known root OS system files
        var fileName = Path.GetFileName(normalized);
        if (string.Equals(fileName, "pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "swapfile.sys", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "dumpstack.log", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string pathWithoutRoot = normalized;
        if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            pathWithoutRoot = normalized.Substring(3);
        }
        else if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var firstSlash = normalized.IndexOf('\\', 2);
            if (firstSlash >= 0)
            {
                var secondSlash = normalized.IndexOf('\\', firstSlash + 1);
                if (secondSlash >= 0)
                {
                    pathWithoutRoot = normalized.Substring(secondSlash + 1);
                }
            }
        }
        else if (normalized.StartsWith('\\'))
        {
            pathWithoutRoot = normalized.TrimStart('\\');
        }

        // WSL and Docker virtual disks or container storage (.vhdx inside AppData or Docker/WSL packages)
        if (normalized.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Docker\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\wsl\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return pathWithoutRoot.StartsWith(@"Windows\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "Windows", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"Program Files\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "Program Files", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"Program Files (x86)\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"ProgramData\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "ProgramData", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"AppData\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\AppData\Local\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"System Volume Information\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
               pathWithoutRoot.StartsWith(@"$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(pathWithoutRoot, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserDownloadsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return UserDownloadsPattern().IsMatch(normalized);
    }
}
