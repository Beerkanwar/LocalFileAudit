using System.IO;
using System.Text;
using System.Text.Json;
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
            _logger.LogError(ex, "Failed to load archive plan from {Path}. Returning empty plan.", _planFilePath);
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
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _planFilePath, overwrite: true);

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

        // 1. Generate ArchiveManifest.csv
        var manifestSb = new StringBuilder();
        manifestSb.AppendLine("BatchName,SourcePath,PlannedDestination,Reason,EstimatedSizeBytes,FileCount,IsVerified");
        foreach (var b in plan.Batches)
        {
            manifestSb.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6}",
                EscapeCsv(b.BatchName),
                EscapeCsv(b.SourcePath),
                EscapeCsv(b.PlannedDestinationPath),
                EscapeCsv(b.Reason),
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
            previewSb.AppendLine($"Write-Host '--- [PREVIEW ONLY] Processing Batch: {b.BatchName} ---' -ForegroundColor Cyan");
            previewSb.AppendLine($"$src = '{b.SourcePath.Replace("'", "''")}'");
            previewSb.AppendLine($"$dst = '{b.PlannedDestinationPath.Replace("'", "''")}'");
            previewSb.AppendLine($"$log = Join-Path $logDir 'preview-{cleanName}.log'");
            previewSb.AppendLine("if (Test-Path $src) {");
            previewSb.AppendLine("    # Robocopy /L = List only (no file copy occurs)");
            previewSb.AppendLine("    robocopy $src $dst /E /COPY:DAT /XJ /L /NP /R:1 /W:1 /LOG+:$log");
            previewSb.AppendLine("    if ($LASTEXITCODE -ge 8) {");
            previewSb.AppendLine($"        throw \"Robocopy preview failed with exit code $LASTEXITCODE for batch '{b.BatchName}'. Check log: $log\"");
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
            copySb.AppendLine($"Write-Host '>>> Starting Copy for Batch: {b.BatchName} >>>' -ForegroundColor Yellow");
            copySb.AppendLine($"$src = '{b.SourcePath.Replace("'", "''")}'");
            copySb.AppendLine($"$dst = '{b.PlannedDestinationPath.Replace("'", "''")}'");
            copySb.AppendLine($"$log = Join-Path $logDir 'copy-{cleanName}.log'");
            copySb.AppendLine("if (Test-Path $src) {");
            copySb.AppendLine("    # Copy with data, attributes, timestamps. Safe retries, skip junctions.");
            copySb.AppendLine("    robocopy $src $dst /E /COPY:DAT /XJ /NP /R:2 /W:3 /LOG+:$log");
            copySb.AppendLine("    if ($LASTEXITCODE -ge 8) {");
            copySb.AppendLine($"        throw \"Robocopy copy failed with exit code $LASTEXITCODE for batch '{b.BatchName}'. Check log: $log\"");
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

    private static string SanitizeIdentifier(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || c == ' ' ? '_' : c);
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
