using System.Data;
using System.Security.Cryptography;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
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
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
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
            WHERE ScanRunId = @ScanRunId AND SizeBytes IN (
                SELECT SizeBytes FROM Files
                WHERE ScanRunId = @ScanRunId AND SizeBytes >= @MinSizeBytes
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

        _logger.LogInformation("Stage 2 duplicate detection: Computing SHA-256 hashes for {Count} candidate files in scan {ScanRunId}", filesToHash.Count, scanRunId);

        int processed = 0;
        long totalBytesHashed = 0;
        var hashedFiles = new List<FileRecord>();

        foreach (var file in filesToHash)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            processed++;
            totalBytesHashed += file.SizeBytes;

            progress?.Report(new HashJobProgress
            {
                FilesProcessed = processed,
                TotalFilesToHash = filesToHash.Count,
                BytesHashed = totalBytesHashed,
                CurrentFilePath = file.FullPath
            });

            if (!string.IsNullOrEmpty(hash))
            {
                hashedFiles.Add(file);
            }
        }

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

            // Check pre-hash timestamp
            var preModified = File.GetLastWriteTimeUtc(fullPath);
            if (expectedLastWriteUtc.HasValue && Math.Abs((preModified - expectedLastWriteUtc.Value).TotalSeconds) > 2)
            {
                _logger.LogWarning("File modified since scan: {Path}. Marking hash as unverified.", fullPath);
            }

            byte[] hashBytes;
            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
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

    private static FileRecord MapFileRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
            FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
            ParentPath = reader.GetString(reader.GetOrdinal("ParentPath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            Extension = reader.GetString(reader.GetOrdinal("Extension")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
            CreationTimeUtc = reader.IsDBNull(reader.GetOrdinal("CreationTimeUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("CreationTimeUtc"))),
            LastWriteTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastWriteTimeUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastWriteTimeUtc"))),
            LastAccessTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastAccessTimeUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastAccessTimeUtc"))),
            Attributes = reader.GetInt32(reader.GetOrdinal("Attributes")),
            ObservedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("ObservedAtUtc"))),
            MetadataStatus = Enum.Parse<ScanMetadataStatus>(reader.GetString(reader.GetOrdinal("MetadataStatus")), ignoreCase: true),
            HashAlgorithm = reader.IsDBNull(reader.GetOrdinal("HashAlgorithm")) ? null : reader.GetString(reader.GetOrdinal("HashAlgorithm")),
            HashValue = reader.IsDBNull(reader.GetOrdinal("HashValue")) ? null : reader.GetString(reader.GetOrdinal("HashValue"))
        };
    }
}
