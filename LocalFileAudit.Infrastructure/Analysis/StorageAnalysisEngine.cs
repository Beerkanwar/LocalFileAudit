using System.Data;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Analysis;

/// <summary>
/// Executes deterministic storage aggregation, file type statistics, and candidate classification via SQLite.
/// </summary>
public sealed class StorageAnalysisEngine : IStorageAnalysisEngine
{
    private readonly IDatabaseInitializer _initializer;
    private readonly IScanRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<StorageAnalysisEngine> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, StorageMetricsSummary> _summaryCache = new();

    public StorageAnalysisEngine(
        IDatabaseInitializer initializer,
        IScanRepository repository,
        IConfigurationService configurationService,
        ILogger<StorageAnalysisEngine> logger)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection($"Data Source={_initializer.DatabasePath};Mode=ReadOnly;Cache=Shared");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private async Task<SqliteConnection> OpenWriteConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection($"Data Source={_initializer.DatabasePath};Mode=ReadWrite;Cache=Shared");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    public void InvalidateCache(Guid? scanRunId = null)
    {
        if (scanRunId.HasValue)
        {
            _summaryCache.TryRemove(scanRunId.Value, out _);
        }
        else
        {
            _summaryCache.Clear();
        }
    }

    public async Task<StorageMetricsSummary> AnalyzeScanAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        if (_summaryCache.TryGetValue(scanRunId, out var cachedSummary))
        {
            return cachedSummary;
        }

        var run = await _repository.GetScanRunAsync(scanRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Scan run {scanRunId} not found.", nameof(scanRunId));

        var totalFiles = await _repository.GetFileCountForScanAsync(scanRunId, cancellationToken).ConfigureAwait(false);
        var totalBytes = await _repository.GetTotalLogicalSizeBytesForScanAsync(scanRunId, cancellationToken).ConfigureAwait(false);

        var topExtensions = await GetExtensionStatisticsAsync(scanRunId, 25, cancellationToken).ConfigureAwait(false);
        var largestFiles = await GetLargestFilesAsync(scanRunId, 50, cancellationToken).ConfigureAwait(false);
        var largestDirectories = await GetLargestDirectoriesAsync(scanRunId, 30, cancellationToken).ConfigureAwait(false);
        var candidates = await ClassifyCandidatesAsync(scanRunId, cancellationToken).ConfigureAwait(false);

        long cloudCount = 0;
        long cloudBytes = 0;
        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var cloudCmd = conn.CreateCommand())
        {
            cloudCmd.CommandText = @"
                SELECT COUNT(1) AS CloudCount, COALESCE(SUM(SizeBytes), 0) AS CloudBytes
                FROM Files
                WHERE ScanRunId = @ScanRunId AND MetadataStatus = 'CloudPlaceholder';
            ";
            cloudCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
            await using var cloudReader = await cloudCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await cloudReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cloudCount = cloudReader.GetInt64(0);
                cloudBytes = cloudReader.GetInt64(1);
            }
        }

        var roots = run.SelectedDrives?.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        long totalCapacity = 0;
        long totalFree = 0;
        var inspectedDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in roots)
        {
            var pathRoot = Path.GetPathRoot(r.Trim());
            if (!string.IsNullOrEmpty(pathRoot) && inspectedDrives.Add(pathRoot))
            {
                var space = Scanning.FileAllocationHelper.GetVolumeSpace(pathRoot);
                if (space.HasValue)
                {
                    totalCapacity += space.Value.TotalBytes;
                    totalFree += space.Value.FreeBytes;
                }
            }
        }


        var summary = new StorageMetricsSummary
        {
            ScanRunId = scanRunId,
            AnalyzedAtUtc = DateTime.UtcNow,
            TotalFiles = totalFiles,
            TotalLogicalSizeBytes = totalBytes,
            CloudPlaceholderCount = cloudCount,
            CloudPlaceholderSizeBytes = cloudBytes,
            VolumeCapacityBytes = totalCapacity,
            VolumeFreeBytes = totalFree,
            ErrorCount = run.ErrorCount,
            IsScanComplete = run.Status == ScanState.Completed,
            SelectedDrives = run.SelectedDrives ?? string.Empty,
            TopExtensions = topExtensions.ToList(),
            LargestFiles = largestFiles.ToList(),
            LargestDirectories = largestDirectories.ToList(),
            ReviewCandidates = candidates.ToList()
        };

        _summaryCache[scanRunId] = summary;
        return summary;
    }

    public async Task<IReadOnlyList<ExtensionSummary>> GetExtensionStatisticsAsync(Guid scanRunId, int top = 50, CancellationToken cancellationToken = default)
    {
        var list = new List<ExtensionSummary>();
        var totalFiles = await _repository.GetFileCountForScanAsync(scanRunId, cancellationToken).ConfigureAwait(false);
        var totalBytes = await _repository.GetTotalLogicalSizeBytesForScanAsync(scanRunId, cancellationToken).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Extension, COUNT(1) AS FileCount, SUM(SizeBytes) AS TotalBytes
            FROM Files
            WHERE ScanRunId = @ScanRunId
            GROUP BY Extension
            ORDER BY TotalBytes DESC
            LIMIT @Top;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@Top", top);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ext = reader.GetString(reader.GetOrdinal("Extension"));
            var count = reader.GetInt64(reader.GetOrdinal("FileCount"));
            var bytes = reader.GetInt64(reader.GetOrdinal("TotalBytes"));

            list.Add(new ExtensionSummary
            {
                Extension = string.IsNullOrEmpty(ext) ? "(no extension)" : ext,
                FileCount = count,
                TotalLogicalSizeBytes = bytes,
                PercentageOfTotalSize = totalBytes > 0 ? (double)bytes / totalBytes * 100.0 : 0,
                PercentageOfTotalFiles = totalFiles > 0 ? (double)count / totalFiles * 100.0 : 0
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<FileRecord>> GetLargestFilesAsync(Guid scanRunId, int top = 100, CancellationToken cancellationToken = default)
    {
        var list = new List<FileRecord>();
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM Files
            WHERE ScanRunId = @ScanRunId
            ORDER BY SizeBytes DESC
            LIMIT @Top;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@Top", top);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapFileRecord(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<DirectorySummary>> GetLargestDirectoriesAsync(Guid scanRunId, int top = 50, CancellationToken cancellationToken = default)
    {
        var list = new List<DirectorySummary>();
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ParentPath, COUNT(1) AS DirectFiles, SUM(SizeBytes) AS DirectBytes
            FROM Files
            WHERE ScanRunId = @ScanRunId
            GROUP BY ParentPath
            ORDER BY DirectBytes DESC
            LIMIT @Top;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@Top", top);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(reader.GetOrdinal("ParentPath"));
            var files = reader.GetInt64(reader.GetOrdinal("DirectFiles"));
            var bytes = reader.GetInt64(reader.GetOrdinal("DirectBytes"));

            list.Add(new DirectorySummary
            {
                DirectoryPath = path,
                DirectFileCount = files,
                DirectSizeBytes = bytes,
                RecursiveFileCount = files,
                RecursiveSizeBytes = bytes,
                IsComplete = true
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<CandidateClassification>> ClassifyCandidatesAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        var candidates = new List<CandidateClassification>();

        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // 1. Check if AnalysisResults table already contains cached classifications for this scan run
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(1) FROM AnalysisResults WHERE ScanRunId = @ScanRunId;";
                checkCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
                var countObj = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var count = countObj is long l ? l : Convert.ToInt64(countObj ?? 0L);

                if (count > 0)
                {
                    await using var loadCmd = conn.CreateCommand();
                    loadCmd.CommandText = @"
                        SELECT ar.FileId, ar.Category, ar.RuleId, ar.Priority, ar.Confidence, ar.Explanation,
                               f.FullPath, f.FileName, f.Extension, f.SizeBytes, f.LastWriteTimeUtc
                        FROM AnalysisResults ar
                        LEFT JOIN Files f ON f.Id = ar.FileId
                        WHERE ar.ScanRunId = @ScanRunId
                        ORDER BY ar.Priority ASC, f.SizeBytes DESC;
                    ";
                    loadCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
                    await using var reader = await loadCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var fileId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                        var categoryStr = reader.GetString(1);
                        var ruleId = reader.GetString(2);
                        var priority = reader.GetInt32(3);
                        var confidence = reader.GetDouble(4);
                        var explanation = reader.GetString(5);
                        var fullPath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                        var fileName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
                        var extension = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                        var sizeBytes = reader.IsDBNull(9) ? 0L : reader.GetInt64(9);
                        var lastWrite = reader.IsDBNull(10) ? (DateTime?)null : DateTime.Parse(reader.GetString(10));

                        Enum.TryParse<CandidateCategory>(categoryStr, out var cat);

                        candidates.Add(new CandidateClassification
                        {
                            FileRecordId = fileId,
                            ScanRunId = scanRunId,
                            Category = cat,
                            RuleId = ruleId,
                            Priority = priority,
                            Confidence = confidence,
                            Explanation = explanation,
                            FullPath = fullPath,
                            FileName = fileName,
                            Extension = extension,
                            SizeBytes = sizeBytes,
                            LastWriteTimeUtc = lastWrite
                        });
                    }

                    return candidates;
                }
            }

            // 2. Cache miss: evaluate rules from Files
            var config = _configurationService.Current;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT * FROM Files
                    WHERE ScanRunId = @ScanRunId
                      AND (
                          SizeBytes >= @LargeThreshold 
                          OR Extension IN ('.zip', '.rar', '.7z', '.tar', '.gz', '.iso', '.vhd', '.vhdx', '.mp4', '.mkv', '.avi', '.mov', '.tmp', '.bak', '.old', '.dmp', '.log')
                          OR FullPath LIKE '%\Downloads\%'
                      )
                    ORDER BY SizeBytes DESC
                    LIMIT 1000;
                ";
                cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
                cmd.Parameters.AddWithValue("@LargeThreshold", config.Analysis.LargeFileThresholdBytes / 2);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var file = MapFileRecord(reader);
                    var classification = CandidateRuleEvaluator.Classify(file, config);

                    if (classification.Category != CandidateCategory.KeepOnLaptop && classification.Category != CandidateCategory.Unknown)
                    {
                        candidates.Add(classification);
                    }
                }
            }
        }

        var sorted = candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.SizeBytes).ToList();

        // 3. Persist classified results to AnalysisResults table
        if (sorted.Count > 0)
        {
            try
            {
                await using var writeConn = await OpenWriteConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var tx = await writeConn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using var insCmd = writeConn.CreateCommand();
                insCmd.Transaction = (SqliteTransaction)tx;
                insCmd.CommandText = @"
                    INSERT INTO AnalysisResults (ScanRunId, FileId, Category, RuleId, Priority, Confidence, Explanation, AnalysisVersion)
                    VALUES (@ScanRunId, @FileId, @Category, @RuleId, @Priority, @Confidence, @Explanation, @AnalysisVersion);
                ";
                var pScan = insCmd.Parameters.Add("@ScanRunId", SqliteType.Text);
                var pFile = insCmd.Parameters.Add("@FileId", SqliteType.Integer);
                var pCat = insCmd.Parameters.Add("@Category", SqliteType.Text);
                var pRule = insCmd.Parameters.Add("@RuleId", SqliteType.Text);
                var pPrio = insCmd.Parameters.Add("@Priority", SqliteType.Integer);
                var pConf = insCmd.Parameters.Add("@Confidence", SqliteType.Real);
                var pExp = insCmd.Parameters.Add("@Explanation", SqliteType.Text);
                var pVer = insCmd.Parameters.Add("@AnalysisVersion", SqliteType.Text);

                foreach (var c in sorted)
                {
                    pScan.Value = scanRunId.ToString();
                    pFile.Value = c.FileRecordId > 0 ? c.FileRecordId : (object)DBNull.Value;
                    pCat.Value = c.Category.ToString();
                    pRule.Value = c.RuleId;
                    pPrio.Value = c.Priority;
                    pConf.Value = c.Confidence;
                    pExp.Value = c.Explanation;
                    pVer.Value = "1.3";
                    await insCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist AnalysisResults cache for scan {ScanRunId}", scanRunId);
            }
        }

        return sorted;
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
