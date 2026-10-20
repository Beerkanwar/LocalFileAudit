using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LocalFileAudit.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Archive;

/// <summary>
/// Strictly read-only archive verification service.
/// Compares source vs destination directories by relative path and validates sizes and SHA-256 hashes.
/// Invariant: Never opens files with write access; never executes move, copy, or delete operations.
/// </summary>
public sealed class ArchiveVerificationService : IArchiveVerificationService
{
    private readonly IAppEnvironment _environment;
    private readonly ILogger<ArchiveVerificationService> _logger;

    public ArchiveVerificationService(
        IAppEnvironment environment,
        ILogger<ArchiveVerificationService> logger)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ArchiveVerificationSummary> VerifyArchiveAsync(
        string sourceDirectory,
        string destinationDirectory,
        bool calculateSha256 = false,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var sw = Stopwatch.StartNew();

        var fullSource = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDest = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(fullSource))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {fullSource}");
        }

        if (!Directory.Exists(fullDest))
        {
            throw new DirectoryNotFoundException($"Destination directory does not exist: {fullDest}");
        }

        // Nested-path guard: prevent overlapping or identical source and destination directories
        ValidatePathsDoNotOverlap(fullSource, fullDest);

        _logger.LogInformation("Starting read-only archive verification between Source={Source} and Dest={Dest} (SHA256={Sha})",
            fullSource, fullDest, calculateSha256);

        var summary = new ArchiveVerificationSummary
        {
            SourceDirectory = fullSource,
            DestinationDirectory = fullDest,
            Sha256Calculated = calculateSha256,
            VerifiedAtUtc = DateTime.UtcNow
        };

        // 1. Enumerate source files
        var sourceFiles = new Dictionary<string, (string FullPath, long SizeBytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(file);
                var rel = Path.GetRelativePath(fullSource, file);
                sourceFiles[rel] = (file, fi.Length);
                summary.TotalSourceFiles++;
                summary.TotalSourceSizeBytes += fi.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect source file {Path}", file);
            }
        }

        // 2. Enumerate destination files
        var destFiles = new Dictionary<string, (string FullPath, long SizeBytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(fullDest, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(file);
                var rel = Path.GetRelativePath(fullDest, file);
                destFiles[rel] = (file, fi.Length);
                summary.TotalDestinationFiles++;
                summary.TotalDestinationSizeBytes += fi.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect destination file {Path}", file);
            }
        }

        var allRelPaths = new HashSet<string>(sourceFiles.Keys, StringComparer.OrdinalIgnoreCase);
        allRelPaths.UnionWith(destFiles.Keys);

        var totalOperations = allRelPaths.Count;
        var processedOperations = 0;

        foreach (var relPath in allRelPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inSource = sourceFiles.TryGetValue(relPath, out var sourceInfo);
            var inDest = destFiles.TryGetValue(relPath, out var destInfo);

            var item = new ArchiveVerificationItem
            {
                RelativePath = relPath,
                SourceFullPath = inSource ? sourceInfo.FullPath : string.Empty,
                DestinationFullPath = inDest ? destInfo.FullPath : string.Empty,
                SourceSizeBytes = inSource ? sourceInfo.SizeBytes : 0,
                DestinationSizeBytes = inDest ? destInfo.SizeBytes : 0
            };

            if (inSource && !inDest)
            {
                item.Status = VerificationStatus.MissingAtDestination;
                item.Details = "File exists at source but is missing at destination.";
                summary.MissingCount++;
            }
            else if (!inSource && inDest)
            {
                item.Status = VerificationStatus.ExtraAtDestination;
                item.Details = "File exists on destination volume but was not present in source.";
                summary.ExtraCount++;
            }
            else if (inSource && inDest)
            {
                if (sourceInfo.SizeBytes != destInfo.SizeBytes)
                {
                    item.Status = VerificationStatus.SizeMismatch;
                    item.Details = $"Size discrepancy: Source={sourceInfo.SizeBytes:N0} B vs Dest={destInfo.SizeBytes:N0} B.";
                    summary.SizeMismatchCount++;
                }
                else if (calculateSha256)
                {
                    // Compute SHA-256 strictly read-only
                    var srcHash = await ComputeFileSha256ReadOnlyAsync(sourceInfo.FullPath, cancellationToken).ConfigureAwait(false);
                    var dstHash = await ComputeFileSha256ReadOnlyAsync(destInfo.FullPath, cancellationToken).ConfigureAwait(false);

                    item.SourceHash = srcHash;
                    item.DestinationHash = dstHash;

                    if (!string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = VerificationStatus.HashMismatch;
                        item.Details = "Hash mismatch: File sizes match, but byte content diverges.";
                        summary.HashMismatchCount++;
                    }
                    else
                    {
                        item.Status = VerificationStatus.Match;
                        item.Details = "Verified match (size and SHA-256 hash match).";
                        summary.MatchCount++;
                    }
                }
                else
                {
                    item.Status = VerificationStatus.Match;
                    item.Details = "Verified match (size matches; hash check was skipped).";
                    summary.MatchCount++;
                }
            }

            summary.Items.Add(item);
            processedOperations++;

            if (totalOperations > 0)
            {
                progress?.Report((double)processedOperations / totalOperations * 100.0);
            }
        }

        sw.Stop();
        summary.Duration = sw.Elapsed;
        _logger.LogInformation("Archive verification complete in {Elapsed}ms. Matches={Matches}, Missing={Missing}, SizeMismatches={SizeMis}, HashMismatches={HashMis}, Extra={Extra}",
            sw.ElapsedMilliseconds, summary.MatchCount, summary.MissingCount, summary.SizeMismatchCount, summary.HashMismatchCount, summary.ExtraCount);

        return summary;
    }

    public async Task<string> ExportVerificationCsvAsync(
        ArchiveVerificationSummary summary,
        string? destinationCsvPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var targetPath = destinationCsvPath ?? Path.Combine(
            _environment.ReportsDirectory,
            $"ArchiveVerification-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".staging_verify_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16384, useAsync: true))
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteLineAsync("Status,RelativePath,SourceSizeBytes,DestinationSizeBytes,SourceHash,DestinationHash,Details,SourceFullPath,DestinationFullPath");

                foreach (var item in summary.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var escapedRel = EscapeCsv(item.RelativePath);
                    var escapedDetails = EscapeCsv(item.Details);
                    var escapedSrc = EscapeCsv(item.SourceFullPath);
                    var escapedDst = EscapeCsv(item.DestinationFullPath);

                    var line = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                        item.Status,
                        escapedRel,
                        item.SourceSizeBytes,
                        item.DestinationSizeBytes,
                        item.SourceHash ?? string.Empty,
                        item.DestinationHash ?? string.Empty,
                        escapedDetails,
                        escapedSrc,
                        escapedDst);

                    await writer.WriteLineAsync(line);
                }

                await writer.FlushAsync();
            }

            File.Move(tempPath, targetPath, overwrite: true);
            _logger.LogInformation("Exported archive verification report to {Path}", targetPath);
            return targetPath;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    private static void ValidatePathsDoNotOverlap(string source, string destination)
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        var altSep = Path.AltDirectorySeparatorChar.ToString();

        var srcNorm = source.Replace(altSep, sep).TrimEnd(Path.DirectorySeparatorChar) + sep;
        var dstNorm = destination.Replace(altSep, sep).TrimEnd(Path.DirectorySeparatorChar) + sep;

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

    private static async Task<string> ComputeFileSha256ReadOnlyAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            // Strictly FileAccess.Read, FileShare.ReadWrite to ensure zero write/locking contention
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            using var sha = SHA256.Create();
            var hashBytes = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return $"ERROR({ex.GetType().Name})";
        }
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
