using System.Data;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Common;
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
        var conn = new SqliteConnection($"Data Source={_initializer.DatabasePath};Mode=ReadOnly;Cache=Private;Default Timeout=30;");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 30000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        var run = await _repository.GetScanRunAsync(scanRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Scan run {scanRunId} not found.", nameof(scanRunId));

        if (run.Status == ScanState.Completed && _summaryCache.TryGetValue(scanRunId, out var cachedSummary) && cachedSummary.IsScanComplete)
        {
            return cachedSummary;
        }

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

        if (run.Status == ScanState.Completed)
        {
            _summaryCache[scanRunId] = summary;
        }
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
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Determine if the scan run is complete
        var isComplete = false;
        await using (var statusCmd = conn.CreateCommand())
        {
            statusCmd.CommandText = "SELECT Status FROM ScanRuns WHERE Id = @ScanRunId LIMIT 1;";
            statusCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
            var statusObj = await statusCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is string statusStr && Enum.TryParse<ScanState>(statusStr, out var state))
            {
                isComplete = state == ScanState.Completed;
            }
        }

        // 2. Load all known directories and direct file aggregates
        var dirMap = new Dictionary<string, DirAggregates>(StringComparer.OrdinalIgnoreCase);

        // Pre-populate with all directories recorded during the scan (even if they contain 0 direct files)
        await using (var dirCmd = conn.CreateCommand())
        {
            dirCmd.CommandText = "SELECT Path FROM Directories WHERE ScanRunId = @ScanRunId;";
            dirCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
            await using var dirReader = await dirCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await dirReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var p = NormalizeDirPath(dirReader.GetString(0));
                if (!string.IsNullOrEmpty(p) && !dirMap.ContainsKey(p))
                {
                    dirMap[p] = new DirAggregates { DirectoryPath = p };
                }
            }
        }

        // Aggregate direct files and sizes per ParentPath
        var directAggregates = new List<(string Path, long Files, long Bytes)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ParentPath, COUNT(1) AS DirectFiles, SUM(SizeBytes) AS DirectBytes
                FROM Files
                WHERE ScanRunId = @ScanRunId
                GROUP BY ParentPath;
            ";
            cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = NormalizeDirPath(reader.GetString(0));
                var files = reader.GetInt64(1);
                var bytes = reader.GetInt64(2);
                directAggregates.Add((path, files, bytes));
            }
        }

        // 3. Roll up direct statistics into recursive totals across ancestor hierarchy
        foreach (var (path, files, bytes) in directAggregates)
        {
            if (!dirMap.TryGetValue(path, out var selfEntry))
            {
                selfEntry = new DirAggregates { DirectoryPath = path };
                dirMap[path] = selfEntry;
            }

            selfEntry.DirectFileCount = files;
            selfEntry.DirectSizeBytes = bytes;
            selfEntry.RecursiveFileCount += files;
            selfEntry.RecursiveSizeBytes += bytes;

            // Propagate up to all parent directories
            var current = path;
            while (true)
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var normalizedParent = NormalizeDirPath(parent);
                if (!dirMap.TryGetValue(normalizedParent, out var parentEntry))
                {
                    parentEntry = new DirAggregates { DirectoryPath = normalizedParent };
                    dirMap[normalizedParent] = parentEntry;
                }

                parentEntry.RecursiveFileCount += files;
                parentEntry.RecursiveSizeBytes += bytes;
                current = normalizedParent;
            }
        }

        // 4. Return top directories ordered by RecursiveSizeBytes descending
        return dirMap.Values
            .OrderByDescending(d => d.RecursiveSizeBytes)
            .ThenByDescending(d => d.DirectSizeBytes)
            .Take(top)
            .Select(d => new DirectorySummary
            {
                DirectoryPath = d.DirectoryPath,
                DirectFileCount = d.DirectFileCount,
                DirectSizeBytes = d.DirectSizeBytes,
                RecursiveFileCount = d.RecursiveFileCount,
                RecursiveSizeBytes = d.RecursiveSizeBytes,
                IsComplete = isComplete
            })
            .ToList();
    }

    private static string NormalizeDirPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim();
        if (trimmed.Length == 3 && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return trimmed.Replace('/', '\\');
        }
        return trimmed.TrimEnd('\\', '/');
    }

    private sealed class DirAggregates
    {
        public string DirectoryPath { get; set; } = string.Empty;
        public long DirectFileCount { get; set; }
        public long DirectSizeBytes { get; set; }
        public long RecursiveFileCount { get; set; }
        public long RecursiveSizeBytes { get; set; }
    }

    public async Task<IReadOnlyList<CandidateClassification>> ClassifyCandidatesAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        // 1. Check if AnalysisResults table already contains cached classifications for this scan run
        var cached = await _repository.GetCachedAnalysisResultsAsync(scanRunId, cancellationToken).ConfigureAwait(false);
        if (cached.Count > 0)
        {
            return cached;
        }

        // 2. Cache miss: evaluate rules from Files
        var candidates = new List<CandidateClassification>();
        var config = _configurationService.Current;
        await using (var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
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

        var sorted = candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.SizeBytes).ToList();

        // 3. Persist classified results to AnalysisResults table via repository
        if (sorted.Count > 0)
        {
            try
            {
                await _repository.SaveAnalysisResultsAsync(scanRunId, sorted, cancellationToken).ConfigureAwait(false);
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
            CreationTimeUtc = reader.IsDBNull(reader.GetOrdinal("CreationTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("CreationTimeUtc"))),
            LastWriteTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastWriteTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("LastWriteTimeUtc"))),
            LastAccessTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastAccessTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("LastAccessTimeUtc"))),
            Attributes = reader.GetInt32(reader.GetOrdinal("Attributes")),
            ObservedAtUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("ObservedAtUtc"))),
            MetadataStatus = Enum.Parse<ScanMetadataStatus>(reader.GetString(reader.GetOrdinal("MetadataStatus")), ignoreCase: true),
            HashAlgorithm = reader.IsDBNull(reader.GetOrdinal("HashAlgorithm")) ? null : reader.GetString(reader.GetOrdinal("HashAlgorithm")),
            HashValue = reader.IsDBNull(reader.GetOrdinal("HashValue")) ? null : reader.GetString(reader.GetOrdinal("HashValue"))
        };
    }
}
