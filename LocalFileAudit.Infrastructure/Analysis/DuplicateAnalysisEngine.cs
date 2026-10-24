using System.Data;
using System.Security.Cryptography;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using LocalFileAudit.Infrastructure.Persistence;
using LocalFileAudit.Infrastructure.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Analysis;

/// <summary>
/// Two-stage duplicate detection engine.
/// Stage 1: Groups files by exact byte size.
/// Stage 2: Asynchronously computes cryptographic SHA-256 hashes to verify matching content.
/// Strictly read-only: Never modifies, deletes, or moves user files.
/// </summary>
public sealed class DuplicateAnalysisEngine : IDuplicateAnalysisEngine
{
    private readonly IDatabaseInitializer _initializer;
    private readonly IScanRepository _repository;
    private readonly ILogger<DuplicateAnalysisEngine> _logger;
    private readonly string _connectionString;

    public DuplicateAnalysisEngine(
        IDatabaseInitializer initializer,
        IScanRepository repository,
        ILogger<DuplicateAnalysisEngine> logger)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _initializer.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 30000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    public async Task<DuplicateAnalysisSummary> FindPotentialDuplicatesBySizeAsync(
        Guid scanRunId,
        long minSizeBytes = 1024,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stage 1 duplicate detection: Finding potential duplicate groups by size for scan {ScanRunId}", scanRunId);

        var candidateFiles = new List<FileRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = @"
            SELECT * FROM Files
            WHERE ScanRunId = @ScanRunId
              AND MetadataStatus = 'Ok'
              AND (Attributes & 1024) = 0
              AND SizeBytes IN (
                SELECT SizeBytes FROM Files
                WHERE ScanRunId = @ScanRunId
                  AND MetadataStatus = 'Ok'
                  AND (Attributes & 1024) = 0
                  AND SizeBytes >= @MinSizeBytes
                GROUP BY SizeBytes HAVING COUNT(1) >= 2
            )
            ORDER BY SizeBytes DESC, FullPath ASC;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@MinSizeBytes", minSizeBytes);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidateFiles.Add(MapFileRecord(reader));
        }

        var groups = candidateFiles
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => (g.Count() - 1) * g.Key)
            .Select(g => new DuplicateGroup
            {
                SizeBytes = g.Key,
                Files = g.ToList()
            })
            .ToList();

        var potentialWastedBytes = groups.Sum(g => g.WastedSizeBytes);
        var totalDuplicateFiles = groups.Sum(g => g.Files.Count);

        return new DuplicateAnalysisSummary
        {
            ScanRunId = scanRunId,
            PotentialGroupsCount = groups.Count,
            VerifiedGroupsCount = 0,
            TotalDuplicateFilesCount = totalDuplicateFiles,
            PotentialWastedSizeBytes = potentialWastedBytes,
            VerifiedWastedSizeBytes = 0,
            Groups = groups
        };
    }

    public async Task<DuplicateAnalysisSummary> ComputeHashesAndVerifyDuplicatesAsync(
        Guid scanRunId,
        long minSizeBytes = 1024,
        IProgress<HashJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stage1Summary = await FindPotentialDuplicatesBySizeAsync(scanRunId, minSizeBytes, cancellationToken).ConfigureAwait(false);

        var filesToHash = stage1Summary.Groups
            .SelectMany(g => g.Files)
            .ToList();

        _logger.LogInformation("Stage 2 duplicate detection: Computing SHA-256 hashes for {Count} candidate files in parallel for scan {ScanRunId}", filesToHash.Count, scanRunId);

        int processed = 0;
        long totalBytesHashed = 0;
        var hashedFiles = new System.Collections.Concurrent.ConcurrentBag<FileRecord>();
        var maxConcurrency = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 4);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var hashTasks = filesToHash.Select(async file =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? hash = file.HashValue;

                // Compute hash if not already cached
                if (string.IsNullOrEmpty(hash) || file.HashAlgorithm != "SHA-256")
                {
                    // Feature S3: Check if any previous run stored a hash for the same (FullPath, SizeBytes, LastWriteTimeUtc)
                    var existingHash = await _repository.FindExistingFileHashAsync(file.FullPath, file.SizeBytes, file.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(existingHash))
                    {
                        hash = existingHash;
                        file.HashAlgorithm = "SHA-256";
                        file.HashValue = hash;
                        await _repository.UpdateFileHashAsync(file.Id, "SHA-256", hash, cancellationToken).ConfigureAwait(false);
                        _logger.LogDebug("Reused existing SHA-256 hash for {FullPath} from prior scan run.", file.FullPath);
                    }
                    else
                    {
                        hash = await ComputeSha256SafeAsync(file.FullPath, file.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(hash))
                        {
                            file.HashAlgorithm = "SHA-256";
                            file.HashValue = hash;
                            await _repository.UpdateFileHashAsync(file.Id, "SHA-256", hash, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                var done = Interlocked.Increment(ref processed);
                var bytes = Interlocked.Add(ref totalBytesHashed, file.SizeBytes);

                progress?.Report(new HashJobProgress
                {
                    FilesProcessed = done,
                    TotalFilesToHash = filesToHash.Count,
                    BytesHashed = bytes,
                    CurrentFilePath = file.FullPath
                });

                if (!string.IsNullOrEmpty(hash))
                {
                    hashedFiles.Add(file);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(hashTasks).ConfigureAwait(false);

        // Group by Size AND SHA-256 Hash
        var verifiedGroups = hashedFiles
            .Where(f => !string.IsNullOrEmpty(f.HashValue))
            .GroupBy(f => (f.SizeBytes, f.HashValue!))
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => (g.Count() - 1) * g.Key.SizeBytes)
            .Select(g => new DuplicateGroup
            {
                SizeBytes = g.Key.SizeBytes,
                HashValue = g.Key.Item2,
                Files = g.ToList()
            })
            .ToList();

        var verifiedWastedBytes = verifiedGroups.Sum(g => g.WastedSizeBytes);

        return new DuplicateAnalysisSummary
        {
            ScanRunId = scanRunId,
            PotentialGroupsCount = stage1Summary.PotentialGroupsCount,
            VerifiedGroupsCount = verifiedGroups.Count,
            TotalDuplicateFilesCount = verifiedGroups.Sum(g => g.Files.Count),
            PotentialWastedSizeBytes = stage1Summary.PotentialWastedSizeBytes,
            VerifiedWastedSizeBytes = verifiedWastedBytes,
            Groups = verifiedGroups
        };
    }

    private async Task<string?> ComputeSha256SafeAsync(string fullPath, DateTime? expectedLastWriteUtc, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("File no longer exists for hashing: {Path}", fullPath);
                return null;
            }

            // Exclude reparse points, junctions, and cloud placeholders to prevent downloading cloud files
            var attr = File.GetAttributes(fullPath);
            if ((attr & FileAttributes.ReparsePoint) != 0 || FilesystemScanner.IsCloudPlaceholder(attr))
            {
                _logger.LogWarning("Skipping cloud placeholder or reparse point file from hashing: {Path}", fullPath);
                return null;
            }

            // Check pre-hash timestamp
            var preModified = File.GetLastWriteTimeUtc(fullPath);
            if (expectedLastWriteUtc.HasValue && Math.Abs((preModified - expectedLastWriteUtc.Value).TotalSeconds) > 1)
            {
                _logger.LogWarning("File modified since scan: {Path}. Discarding hash for safety.", fullPath);
                return null;
            }

            byte[] hashBytes;
            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true))
            {
                hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            // Check post-hash timestamp for concurrence verification
            var postModified = File.GetLastWriteTimeUtc(fullPath);
            if (preModified != postModified)
            {
                _logger.LogWarning("File modified during hashing: {Path}. Hash discarded for safety.", fullPath);
                return null;
            }

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute SHA-256 for file {Path}", fullPath);
            return null;
        }
    }

    private static FileRecord MapFileRecord(SqliteDataReader reader) => FileRecordMapper.Map(reader);
}
