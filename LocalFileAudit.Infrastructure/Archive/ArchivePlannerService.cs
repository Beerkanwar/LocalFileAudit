using System.IO;
using System.Text;
using System.Text.Json;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Archive;

public sealed class ArchivePlannerService : IArchivePlannerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppEnvironment _environment;
    private readonly ILogger<ArchivePlannerService> _logger;
    private readonly string _planFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ArchivePlannerService(
        IAppEnvironment environment,
        ILogger<ArchivePlannerService> logger)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _planFilePath = Path.Combine(_environment.ApplicationDataDirectory, "archive-plans.json");
    }

    public async Task<ArchivePlan> LoadPlanAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadPlanUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ArchivePlan> LoadPlanUnlockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_planFilePath))
            {
                return new ArchivePlan();
            }

            var json = await File.ReadAllTextAsync(_planFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var plan = JsonSerializer.Deserialize<ArchivePlan>(json, JsonOptions);
            return plan ?? new ArchivePlan();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load archive plan from {Path}. Backing up corrupted file.", _planFilePath);
            try
            {
                if (File.Exists(_planFilePath))
                {
                    var backupPath = $"{_planFilePath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                    File.Copy(_planFilePath, backupPath, overwrite: true);
                    PruneOldCorruptedBackups(Path.GetDirectoryName(_planFilePath) ?? ".", $"{Path.GetFileName(_planFilePath)}.corrupted.*.bak", 5);
                }
            }
            catch
            {
                // Best effort backup
            }
            return new ArchivePlan();
        }
    }

    public async Task SavePlanAsync(ArchivePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SavePlanUnlockedAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SavePlanUnlockedAsync(ArchivePlan plan, CancellationToken cancellationToken)
    {
        plan.LastModifiedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(plan, JsonOptions);

        var dir = Path.GetDirectoryName(_planFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".staging_plan_{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _planFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        _logger.LogInformation("Successfully saved archive plan ({BatchCount} batches) to {Path}", plan.Batches.Count, _planFilePath);
    }

    public (long TotalBytes, long FreeBytes)? GetDestinationFreeSpace(string destinationPath)
    {
        return FileAllocationHelper.GetVolumeSpace(destinationPath);
    }

    public async Task MarkBatchVerifiedAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await LoadPlanUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var batch = plan.Batches.FirstOrDefault(b => b.Id == batchId);
            if (batch != null)
            {
                batch.IsVerified = true;
                batch.VerifiedAtUtc = DateTime.UtcNow;
                await SavePlanUnlockedAsync(plan, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Marked batch {BatchId} as verified in archive plan.", batchId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ArchiveScriptGenerationResult> GenerateArchiveScriptsAsync(
        ArchivePlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        var manifestPath = Path.Combine(outputDirectory, "ArchiveManifest.csv");
        var previewScriptPath = Path.Combine(outputDirectory, "preview-archive.ps1");
        var copyScriptPath = Path.Combine(outputDirectory, "copy-archive.ps1");

        // 0. Validate batch paths do not overlap
        foreach (var b in plan.Batches)
        {
            ValidatePathsDoNotOverlap(b.SourcePath, b.PlannedDestinationPath);
        }

        // 1. Generate ArchiveManifest.csv
        var manifestSb = new StringBuilder();
        manifestSb.AppendLine("BatchName,SourcePath,PlannedDestination,Reason,EstimatedSizeBytes,FileCount,IsVerified");
        foreach (var b in plan.Batches)
        {
            manifestSb.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6}",
                CsvSanitizer.EscapeCsv(b.BatchName),
                CsvSanitizer.EscapeCsv(b.SourcePath),
                CsvSanitizer.EscapeCsv(b.PlannedDestinationPath),
                CsvSanitizer.EscapeCsv(b.Reason),
                b.EstimatedSizeBytes,
                b.EstimatedFileCount,
                b.IsVerified));
        }
        await File.WriteAllTextAsync(manifestPath, manifestSb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // 2. Generate preview-archive.ps1 (Strictly Robocopy /L)
        var previewSb = new StringBuilder();
        previewSb.AppendLine("# ==========================================================================");
        previewSb.AppendLine("# REVIEW BEFORE RUNNING — this app never executes file operations");
        previewSb.AppendLine("# This script previews archive operations in READ-ONLY mode using robocopy /L");
        previewSb.AppendLine("# No files will be moved, modified, or copied by this preview script.");
        previewSb.AppendLine("# ==========================================================================");
        previewSb.AppendLine();
        previewSb.AppendLine("$ErrorActionPreference = 'Stop'");
        previewSb.AppendLine("if (Test-Path variable:global:PSNativeCommandUseErrorActionPreference) {");
        previewSb.AppendLine("    $PSNativeCommandUseErrorActionPreference = $false");
        previewSb.AppendLine("}");
        previewSb.AppendLine("$logDir = Join-Path $PSScriptRoot 'logs'");
        previewSb.AppendLine("if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }");
        previewSb.AppendLine();

        foreach (var b in plan.Batches)
        {
            var cleanName = SanitizeIdentifier(b.BatchName);
            var escapedCleanName = EscapePowerShellSingleQuotedLiteral(cleanName);
            var escapedBatchName = EscapePowerShellSingleQuotedLiteral(b.BatchName);
            var escapedSrc = EscapePowerShellSingleQuotedLiteral(b.SourcePath);
            var escapedDst = EscapePowerShellSingleQuotedLiteral(b.PlannedDestinationPath);

            previewSb.AppendLine($"Write-Host ('--- [PREVIEW ONLY] Processing Batch: ' + '{escapedBatchName}' + ' ---') -ForegroundColor Cyan");
            previewSb.AppendLine($"$src = '{escapedSrc}'");
            previewSb.AppendLine($"$dst = '{escapedDst}'");
            previewSb.AppendLine("# Prevent robocopy CLI parser treating a trailing backslash as escaping the wrapping quote");
            previewSb.AppendLine("$safeSrc = if ($src.EndsWith('\\') -and -not $src.EndsWith(':\\')) { $src.TrimEnd('\\') } elseif ($src.EndsWith(':\\')) { $src + '.' } else { $src }");
            previewSb.AppendLine("$safeDst = if ($dst.EndsWith('\\') -and -not $dst.EndsWith(':\\')) { $dst.TrimEnd('\\') } elseif ($dst.EndsWith(':\\')) { $dst + '.' } else { $dst }");
            previewSb.AppendLine($"$log = Join-Path $logDir 'preview-{escapedCleanName}.log'");
            previewSb.AppendLine("if (Test-Path $src) {");
            previewSb.AppendLine("    # Robocopy /L = List only (no file copy occurs)");
            previewSb.AppendLine("    robocopy $safeSrc $safeDst /E /COPY:DAT /XJ /L /NP /R:1 /W:1 /LOG+:$log");
            previewSb.AppendLine("    if ($LASTEXITCODE -ge 8) {");
            previewSb.AppendLine($"        throw ('Robocopy preview failed with exit code ' + $LASTEXITCODE + ' for batch ''' + '{escapedBatchName}' + '''. Check log: ' + $log)");
            previewSb.AppendLine("    }");
            previewSb.AppendLine("} else {");
            previewSb.AppendLine("    Write-Warning \"Source path not found: $src\"");
            previewSb.AppendLine("}");
            previewSb.AppendLine();
        }
        previewSb.AppendLine("Write-Host 'Preview complete! Inspect log files in ./logs before proceeding.' -ForegroundColor Green");
        await File.WriteAllTextAsync(previewScriptPath, previewSb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // 3. Generate copy-archive.ps1 (Real copy with review warnings)
        var copySb = new StringBuilder();
        copySb.AppendLine("# ==========================================================================");
        copySb.AppendLine("# REVIEW BEFORE RUNNING — MANUAL HUMAN EXECUTION REQUIRED");
        copySb.AppendLine("# This application planned this copy script but NEVER executes file operations.");
        copySb.AppendLine("# Run this script yourself in PowerShell after verifying external disk space.");
        copySb.AppendLine("# Safe copy mode: Copies files without deleting source originals.");
        copySb.AppendLine("# ==========================================================================");
        copySb.AppendLine();
        copySb.AppendLine("$ErrorActionPreference = 'Stop'");
        copySb.AppendLine("if (Test-Path variable:global:PSNativeCommandUseErrorActionPreference) {");
        copySb.AppendLine("    $PSNativeCommandUseErrorActionPreference = $false");
        copySb.AppendLine("}");
        copySb.AppendLine("$logDir = Join-Path $PSScriptRoot 'logs'");
        copySb.AppendLine("if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }");
        copySb.AppendLine();

        foreach (var b in plan.Batches)
        {
            var cleanName = SanitizeIdentifier(b.BatchName);
            var escapedCleanName = EscapePowerShellSingleQuotedLiteral(cleanName);
            var escapedBatchName = EscapePowerShellSingleQuotedLiteral(b.BatchName);
            var escapedSrc = EscapePowerShellSingleQuotedLiteral(b.SourcePath);
            var escapedDst = EscapePowerShellSingleQuotedLiteral(b.PlannedDestinationPath);

            copySb.AppendLine($"Write-Host ('>>> Starting Copy for Batch: ' + '{escapedBatchName}' + ' >>>') -ForegroundColor Yellow");
            copySb.AppendLine($"$src = '{escapedSrc}'");
            copySb.AppendLine($"$dst = '{escapedDst}'");
            copySb.AppendLine("# Prevent robocopy CLI parser treating a trailing backslash as escaping the wrapping quote");
            copySb.AppendLine("$safeSrc = if ($src.EndsWith('\\') -and -not $src.EndsWith(':\\')) { $src.TrimEnd('\\') } elseif ($src.EndsWith(':\\')) { $src + '.' } else { $src }");
            copySb.AppendLine("$safeDst = if ($dst.EndsWith('\\') -and -not $dst.EndsWith(':\\')) { $dst.TrimEnd('\\') } elseif ($dst.EndsWith(':\\')) { $dst + '.' } else { $dst }");
            copySb.AppendLine($"$log = Join-Path $logDir 'copy-{escapedCleanName}.log'");
            copySb.AppendLine("if (Test-Path $src) {");
            copySb.AppendLine("    if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }");
            copySb.AppendLine("    # Copy with data, attributes, timestamps. Safe retries, skip junctions, exclude older files (/XO), preserve directory timestamps (/DCOPY:DAT).");
            copySb.AppendLine("    robocopy $safeSrc $safeDst /E /COPY:DAT /DCOPY:DAT /XO /XJ /NP /R:2 /W:3 /LOG+:$log");
            copySb.AppendLine("    if ($LASTEXITCODE -ge 8) {");
            copySb.AppendLine($"        throw ('Robocopy copy failed with exit code ' + $LASTEXITCODE + ' for batch ''' + '{escapedBatchName}' + '''. Check log: ' + $log)");
            copySb.AppendLine("    }");
            copySb.AppendLine("} else {");
            copySb.AppendLine("    Write-Warning \"Source path not found: $src\"");
            copySb.AppendLine("}");
            copySb.AppendLine();
        }
        copySb.AppendLine("Write-Host 'Copy completed! Use Archive Verifier in LocalFileAudit to compare source and destination.' -ForegroundColor Green");
        await File.WriteAllTextAsync(copyScriptPath, copySb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Archive plan scripts generated at {OutputDir}", outputDirectory);

        return new ArchiveScriptGenerationResult
        {
            ManifestCsvPath = manifestPath,
            PreviewScriptPath = previewScriptPath,
            CopyScriptPath = copyScriptPath
        };
    }

    public async Task<DirectoryEstimationResult> EstimateDirectoryAsync(
        string directoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new DirectoryEstimationResult { TotalBytes = 0, FileCount = 0, IsPartial = false };
        }

        return await Task.Run(() =>
        {
            long totalBytes = 0;
            int fileCount = 0;
            bool isPartial = false;
            var stack = new Stack<string>();
            stack.Push(directoryPath);

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                normalizedRoot = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDir = stack.Pop();

                try
                {
                    string normalizedCurrent;
                    try
                    {
                        normalizedCurrent = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    catch
                    {
                        normalizedCurrent = currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }

                    if (!string.Equals(normalizedCurrent, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var dirInfo = new DirectoryInfo(currentDir);
                        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            _logger.LogDebug("Skipping reparse point / junction in directory estimation: {Path}", currentDir);
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check directory attributes for {Directory}", currentDir);
                    isPartial = true;
                    continue;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(currentDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate files in {Directory}", currentDir);
                    isPartial = true;
                    files = Array.Empty<string>();
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        var isReparse = (fi.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (isReparse && !FilesystemScanner.IsCloudPlaceholder(fi.Attributes))
                        {
                            continue;
                        }
                        totalBytes += fi.Length;
                        fileCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to inspect file size for {File}", file);
                        isPartial = true;
                    }
                }

                string[] subdirs;
                try
                {
                    subdirs = Directory.GetDirectories(currentDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate subdirectories in {Directory}", currentDir);
                    isPartial = true;
                    continue;
                }

                foreach (var subdir in subdirs)
                {
                    try
                    {
                        var di = new DirectoryInfo(subdir);
                        var isReparse = (di.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (isReparse && !FilesystemScanner.IsCloudPlaceholder(di.Attributes))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        // Proceed to push if attributes cannot be read
                    }
                    stack.Push(subdir);
                }

                if (fileCount % 100 == 0 || stack.Count == 0)
                {
                    progress?.Report($"Estimating: {fileCount:N0} files scanned...");
                }
            }

            return new DirectoryEstimationResult
            {
                TotalBytes = totalBytes,
                FileCount = fileCount,
                IsPartial = isPartial
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public static string EscapePowerShellSingleQuotedLiteral(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = value.Replace("\r", " ").Replace("\n", " ");
        // PowerShell parser treats ASCII single-quote (') and Unicode single-quotes (‘, ’, ‚, ‛) as quote delimiters
        return sanitized
            .Replace("'", "''")
            .Replace("‘", "‘‘")
            .Replace("’", "’’")
            .Replace("‚", "‚‚")
            .Replace("‛", "‛‛");
    }

    public static string SanitizeIdentifier(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            // Strip invalid filename characters, spaces, and PowerShell quote & meta-characters (ASCII and Unicode)
            var isDangerous = invalid.Contains(c) ||
                              c == ' ' ||
                              c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛' ||
                              c == '"' || c == '“' || c == '”' || c == '„' ||
                              c == ';' || c == '#' || c == '`' || c == '$';
            sb.Append(isDangerous ? '_' : c);
        }
        return sb.ToString();
    }

    public static void ValidatePathsDoNotOverlap(string source, string destination)
    {
        PathOverlapValidator.ValidateDoNotOverlap(source, destination);
    }

    private static void PruneOldCorruptedBackups(string directory, string searchPattern, int retainMax)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = new DirectoryInfo(directory).GetFiles(searchPattern)
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(retainMax);

            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
