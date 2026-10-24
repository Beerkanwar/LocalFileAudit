using System.Data;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Persistence;

/// <summary>
/// Crash-safe SQLite repository with explicit bounded transactions.
/// </summary>
public sealed class SqliteScanRepository : IScanRepository
{
    private readonly IDatabaseInitializer _initializer;
    private readonly ILogger<SqliteScanRepository> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;

    public SqliteScanRepository(IDatabaseInitializer initializer, ILogger<SqliteScanRepository> logger)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _initializer.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 30000; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    public async Task CreateScanRunAsync(ScanRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ScanRuns (Id, StartTimeUtc, EndTimeUtc, Status, SelectedDrives, ConfigurationSnapshot, FilesDiscovered, DirectoriesCompleted, ErrorCount, ApplicationVersion)
                VALUES (@Id, @StartTimeUtc, @EndTimeUtc, @Status, @SelectedDrives, @ConfigurationSnapshot, @FilesDiscovered, @DirectoriesCompleted, @ErrorCount, @ApplicationVersion);
            ";
            cmd.Parameters.AddWithValue("@Id", run.Id.ToString());
            cmd.Parameters.AddWithValue("@StartTimeUtc", run.StartTimeUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@EndTimeUtc", (object?)run.EndTimeUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", run.Status.ToString());
            cmd.Parameters.AddWithValue("@SelectedDrives", run.SelectedDrives);
            cmd.Parameters.AddWithValue("@ConfigurationSnapshot", run.ConfigurationSnapshot);
            cmd.Parameters.AddWithValue("@FilesDiscovered", run.FilesDiscovered);
            cmd.Parameters.AddWithValue("@DirectoriesCompleted", run.DirectoriesCompleted);
            cmd.Parameters.AddWithValue("@ErrorCount", run.ErrorCount);
            cmd.Parameters.AddWithValue("@ApplicationVersion", run.ApplicationVersion);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateScanRunStatusAsync(
        Guid scanRunId,
        ScanState state,
        DateTime? endTimeUtc,
        long filesDiscovered,
        long dirsCompleted,
        long errorCount,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE ScanRuns
                SET Status = @Status,
                    EndTimeUtc = @EndTimeUtc,
                    FilesDiscovered = @FilesDiscovered,
                    DirectoriesCompleted = @DirectoriesCompleted,
                    ErrorCount = @ErrorCount
                WHERE Id = @Id;
            ";
            cmd.Parameters.AddWithValue("@Id", scanRunId.ToString());
            cmd.Parameters.AddWithValue("@Status", state.ToString());
            cmd.Parameters.AddWithValue("@EndTimeUtc", (object?)endTimeUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FilesDiscovered", filesDiscovered);
            cmd.Parameters.AddWithValue("@DirectoriesCompleted", dirsCompleted);
            cmd.Parameters.AddWithValue("@ErrorCount", errorCount);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (state == ScanState.InProgress)
            {
                await using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM AnalysisResults WHERE ScanRunId = @Id;";
                delCmd.Parameters.AddWithValue("@Id", scanRunId.ToString());
                await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (state == ScanState.Completed)
            {
                try
                {
                    await using var cpCmd = connection.CreateCommand();
                    cpCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                    await cpCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Passive checkpoint after scan completion skipped.");
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ScanRun?> GetScanRunAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanRuns WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", scanRunId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MapScanRun(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<ScanRun>> GetRecentScanRunsAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        var runs = new List<ScanRun>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanRuns ORDER BY StartTimeUtc DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@Limit", count);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(MapScanRun(reader));
        }

        return runs;
    }

    public async Task<IReadOnlyList<ScanRun>> GetIncompleteScanRunsAsync(CancellationToken cancellationToken = default)
    {
        var runs = new List<ScanRun>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM ScanRuns 
            WHERE Status IN ('Pending', 'InProgress', 'Paused')
            ORDER BY StartTimeUtc DESC;
        ";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(MapScanRun(reader));
        }

        return runs;
    }

    public async Task DiscardScanRunAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Discarding scan run {ScanRunId} in bounded chunks...", scanRunId);
        var idStr = scanRunId.ToString();

        // 1. Delete Files in bounded batches (50,000 rows per transaction) to prevent holding exclusive locks
        while (!cancellationToken.IsCancellationRequested)
        {
            int deletedInBatch = 0;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM Files 
                    WHERE Id IN (SELECT Id FROM Files WHERE ScanRunId = @Id LIMIT 50000);
                ";
                cmd.Parameters.AddWithValue("@Id", idStr);
                deletedInBatch = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (deletedInBatch == 0) break;
            await Task.Yield();
        }

        // 2. Delete Directories in bounded batches (25,000 rows per transaction)
        while (!cancellationToken.IsCancellationRequested)
        {
            int deletedInBatch = 0;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM Directories 
                    WHERE Id IN (SELECT Id FROM Directories WHERE ScanRunId = @Id LIMIT 25000);
                ";
                cmd.Parameters.AddWithValue("@Id", idStr);
                deletedInBatch = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (deletedInBatch == 0) break;
            await Task.Yield();
        }

        // 3. Delete remaining child tables and parent ScanRuns record
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM ScanErrors WHERE ScanRunId = @Id;
                DELETE FROM AnalysisResults WHERE ScanRunId = @Id;
                DELETE FROM AiReports WHERE ScanRunId = @Id;
                DELETE FROM ChatMessages WHERE ScanRunId = @Id;
                DELETE FROM ScanMetrics WHERE ScanRunId = @Id;
                DELETE FROM ScanRuns WHERE Id = @Id;
            ";
            cmd.Parameters.AddWithValue("@Id", idStr);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            // Reclaim disk space by truncating WAL
            try
            {
                await using var cpCmd = connection.CreateCommand();
                cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cpCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WAL checkpoint truncate after discard skipped.");
            }

            _logger.LogInformation("Discarded scan run {ScanRunId} from local database safely.", scanRunId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertDirectoryBatchAsync(IEnumerable<DirectoryRecord> directories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var list = directories.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Directories (ScanRunId, Path, ParentPath, Status, FilesProcessed, LastCheckpointUtc, LastError)
                VALUES (@ScanRunId, @Path, @ParentPath, @Status, @FilesProcessed, @LastCheckpointUtc, @LastError)
                ON CONFLICT(ScanRunId, Path) DO UPDATE SET
                    LastCheckpointUtc = excluded.LastCheckpointUtc
                RETURNING Id;
            ";

            var pScanRunId = cmd.Parameters.Add("@ScanRunId", SqliteType.Text);
            var pPath = cmd.Parameters.Add("@Path", SqliteType.Text);
            var pParentPath = cmd.Parameters.Add("@ParentPath", SqliteType.Text);
            var pStatus = cmd.Parameters.Add("@Status", SqliteType.Text);
            var pFilesProcessed = cmd.Parameters.Add("@FilesProcessed", SqliteType.Integer);
            var pLastCheckpointUtc = cmd.Parameters.Add("@LastCheckpointUtc", SqliteType.Text);
            var pLastError = cmd.Parameters.Add("@LastError", SqliteType.Text);

            foreach (var dir in list)
            {
                pScanRunId.Value = dir.ScanRunId.ToString();
                pPath.Value = dir.Path;
                pParentPath.Value = (object?)dir.ParentPath ?? DBNull.Value;
                pStatus.Value = dir.Status.ToString();
                pFilesProcessed.Value = dir.FilesProcessed;
                pLastCheckpointUtc.Value = dir.LastCheckpointUtc.ToString("o");
                pLastError.Value = (object?)dir.LastError ?? DBNull.Value;

                var idScalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (idScalar != null && idScalar != DBNull.Value)
                {
                    dir.Id = Convert.ToInt64(idScalar);
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateDirectoryStatusBatchAsync(
        IEnumerable<DirectoryStatusUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var list = updates.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE Directories
                SET Status = @Status,
                    FilesProcessed = @FilesProcessed,
                    LastCheckpointUtc = @LastCheckpointUtc,
                    LastError = @LastError
                WHERE Id = @Id;
            ";
            var pId = cmd.Parameters.Add("@Id", SqliteType.Integer);
            var pStatus = cmd.Parameters.Add("@Status", SqliteType.Text);
            var pFiles = cmd.Parameters.Add("@FilesProcessed", SqliteType.Integer);
            var pTime = cmd.Parameters.Add("@LastCheckpointUtc", SqliteType.Text);
            var pErr = cmd.Parameters.Add("@LastError", SqliteType.Text);

            var nowIso = DateTime.UtcNow.ToString("o");
            foreach (var u in list)
            {
                pId.Value = u.DirectoryId;
                pStatus.Value = u.Status.ToString();
                pFiles.Value = u.FilesProcessed;
                pTime.Value = nowIso;
                pErr.Value = (object?)u.LastError ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpdateDirectoryStatusAsync(
        long directoryId,
        DirectoryState status,
        int filesProcessed,
        string? lastError,
        CancellationToken cancellationToken = default)
        => UpdateDirectoryStatusBatchAsync(new[] { new DirectoryStatusUpdate(directoryId, status, filesProcessed, lastError) }, cancellationToken);

    public async Task<IReadOnlyList<DirectoryRecord>> GetPendingOrInterruptedDirectoriesAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        var dirs = new List<DirectoryRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM Directories 
            WHERE ScanRunId = @ScanRunId AND Status IN ('Pending', 'InProgress')
            ORDER BY Id ASC;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dirs.Add(MapDirectoryRecord(reader));
        }

        return dirs;
    }

    public async Task<long> GetCompletedDirectoryCountForScanAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Directories WHERE ScanRunId = @ScanRunId AND Status = 'Completed';";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task InsertFileBatchAsync(IEnumerable<FileRecord> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var list = files.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;

            // INSERT OR IGNORE safely rejects duplicate entries if directories are re-enumerated on resume
            cmd.CommandText = @"
                INSERT OR IGNORE INTO Files 
                (ScanRunId, FullPath, ParentPath, FileName, Extension, SizeBytes, CreationTimeUtc, LastWriteTimeUtc, LastAccessTimeUtc, Attributes, ObservedAtUtc, MetadataStatus, HashAlgorithm, HashValue)
                VALUES 
                (@ScanRunId, @FullPath, @ParentPath, @FileName, @Extension, @SizeBytes, @CreationTimeUtc, @LastWriteTimeUtc, @LastAccessTimeUtc, @Attributes, @ObservedAtUtc, @MetadataStatus, @HashAlgorithm, @HashValue);
            ";

            var pScanRunId = cmd.Parameters.Add("@ScanRunId", SqliteType.Text);
            var pFullPath = cmd.Parameters.Add("@FullPath", SqliteType.Text);
            var pParentPath = cmd.Parameters.Add("@ParentPath", SqliteType.Text);
            var pFileName = cmd.Parameters.Add("@FileName", SqliteType.Text);
            var pExtension = cmd.Parameters.Add("@Extension", SqliteType.Text);
            var pSizeBytes = cmd.Parameters.Add("@SizeBytes", SqliteType.Integer);
            var pCreationTimeUtc = cmd.Parameters.Add("@CreationTimeUtc", SqliteType.Text);
            var pLastWriteTimeUtc = cmd.Parameters.Add("@LastWriteTimeUtc", SqliteType.Text);
            var pLastAccessTimeUtc = cmd.Parameters.Add("@LastAccessTimeUtc", SqliteType.Text);
            var pAttributes = cmd.Parameters.Add("@Attributes", SqliteType.Integer);
            var pObservedAtUtc = cmd.Parameters.Add("@ObservedAtUtc", SqliteType.Text);
            var pMetadataStatus = cmd.Parameters.Add("@MetadataStatus", SqliteType.Text);
            var pHashAlgorithm = cmd.Parameters.Add("@HashAlgorithm", SqliteType.Text);
            var pHashValue = cmd.Parameters.Add("@HashValue", SqliteType.Text);

            foreach (var f in list)
            {
                pScanRunId.Value = f.ScanRunId.ToString();
                pFullPath.Value = f.FullPath;
                pParentPath.Value = f.ParentPath;
                pFileName.Value = f.FileName;
                pExtension.Value = f.Extension;
                pSizeBytes.Value = f.SizeBytes;
                pCreationTimeUtc.Value = (object?)f.CreationTimeUtc?.ToString("o") ?? DBNull.Value;
                pLastWriteTimeUtc.Value = (object?)f.LastWriteTimeUtc?.ToString("o") ?? DBNull.Value;
                pLastAccessTimeUtc.Value = (object?)f.LastAccessTimeUtc?.ToString("o") ?? DBNull.Value;
                pAttributes.Value = f.Attributes;
                pObservedAtUtc.Value = f.ObservedAtUtc.ToString("o");
                pMetadataStatus.Value = f.MetadataStatus.ToString();
                pHashAlgorithm.Value = (object?)f.HashAlgorithm ?? DBNull.Value;
                pHashValue.Value = (object?)f.HashValue ?? DBNull.Value;

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> GetFileCountForScanAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Files WHERE ScanRunId = @ScanRunId;";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task<long> GetTotalLogicalSizeBytesForScanAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(SizeBytes), 0) FROM Files WHERE ScanRunId = @ScanRunId;";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task InsertScanErrorAsync(ScanErrorRecord error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ScanErrors (ScanRunId, Path, Operation, ErrorCode, ErrorMessage, TimestampUtc)
                VALUES (@ScanRunId, @Path, @Operation, @ErrorCode, @ErrorMessage, @TimestampUtc);
            ";
            cmd.Parameters.AddWithValue("@ScanRunId", error.ScanRunId.ToString());
            cmd.Parameters.AddWithValue("@Path", error.Path);
            cmd.Parameters.AddWithValue("@Operation", error.Operation);
            cmd.Parameters.AddWithValue("@ErrorCode", (object?)error.ErrorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", error.ErrorMessage);
            cmd.Parameters.AddWithValue("@TimestampUtc", error.TimestampUtc.ToString("o"));

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ScanErrorRecord>> GetScanErrorsAsync(Guid scanRunId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var errors = new List<ScanErrorRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScanErrors WHERE ScanRunId = @ScanRunId ORDER BY TimestampUtc DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
        cmd.Parameters.AddWithValue("@Limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            errors.Add(new ScanErrorRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
                Path = reader.GetString(reader.GetOrdinal("Path")),
                Operation = reader.GetString(reader.GetOrdinal("Operation")),
                ErrorCode = reader.IsDBNull(reader.GetOrdinal("ErrorCode")) ? null : reader.GetString(reader.GetOrdinal("ErrorCode")),
                ErrorMessage = reader.GetString(reader.GetOrdinal("ErrorMessage")),
                TimestampUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("TimestampUtc")))
            });
        }

        return errors;
    }

    public async Task<PagedResult<FileRecord>> QueryFilesAsync(FileInventoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var whereConditions = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (query.ScanRunId.HasValue)
        {
            whereConditions.Add("ScanRunId = @ScanRunId");
            parameters.Add(new SqliteParameter("@ScanRunId", query.ScanRunId.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var escapedSearch = EscapeLike(query.SearchText.Trim());
            whereConditions.Add("(FileName LIKE @SearchText ESCAPE '\\' OR FullPath LIKE @SearchText ESCAPE '\\')");
            parameters.Add(new SqliteParameter("@SearchText", $"%{escapedSearch}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Extension))
        {
            var ext = query.Extension.Trim().ToLowerInvariant();
            if (!ext.StartsWith('.')) ext = "." + ext;
            whereConditions.Add("Extension = @Extension");
            parameters.Add(new SqliteParameter("@Extension", ext));
        }

        if (!string.IsNullOrWhiteSpace(query.DriveOrPrefix))
        {
            var escapedPrefix = EscapeLike(query.DriveOrPrefix.Trim());
            whereConditions.Add("FullPath LIKE @Prefix ESCAPE '\\'");
            parameters.Add(new SqliteParameter("@Prefix", $"{escapedPrefix}%"));
        }

        if (query.MinSizeBytes.HasValue)
        {
            whereConditions.Add("SizeBytes >= @MinSizeBytes");
            parameters.Add(new SqliteParameter("@MinSizeBytes", query.MinSizeBytes.Value));
        }

        if (query.MaxSizeBytes.HasValue)
        {
            whereConditions.Add("SizeBytes <= @MaxSizeBytes");
            parameters.Add(new SqliteParameter("@MaxSizeBytes", query.MaxSizeBytes.Value));
        }

        if (query.MinModifiedUtc.HasValue)
        {
            whereConditions.Add("LastWriteTimeUtc >= @MinModifiedUtc");
            parameters.Add(new SqliteParameter("@MinModifiedUtc", query.MinModifiedUtc.Value.ToString("o")));
        }

        if (query.MaxModifiedUtc.HasValue)
        {
            whereConditions.Add("LastWriteTimeUtc <= @MaxModifiedUtc");
            parameters.Add(new SqliteParameter("@MaxModifiedUtc", query.MaxModifiedUtc.Value.ToString("o")));
        }

        if (query.AfterId.HasValue)
        {
            whereConditions.Add("Id > @AfterId");
            parameters.Add(new SqliteParameter("@AfterId", query.AfterId.Value));
        }

        var whereClause = whereConditions.Count > 0 ? "WHERE " + string.Join(" AND ", whereConditions) : "";

        long totalCount;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(1) FROM Files {whereClause};";
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            var countResult = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            totalCount = Convert.ToInt64(countResult);
        }

        var isKeysetPaging = query.AfterId.HasValue || string.Equals(query.SortBy, "Id", StringComparison.OrdinalIgnoreCase);
        var sortColumn = isKeysetPaging
            ? "Id"
            : (query.SortBy?.ToLowerInvariant() switch
            {
                "filename" => "FileName",
                "extension" => "Extension",
                "lastwritetimeutc" => "LastWriteTimeUtc",
                "fullpath" => "FullPath",
                _ => "SizeBytes"
            });
        var sortDirection = (isKeysetPaging && query.SortBy == null) ? "ASC" : (query.SortDescending ? "DESC" : "ASC");

        var pageSize = Math.Clamp(query.Limit, 1, 5000);
        var offset = Math.Max(0, query.Offset);

        var items = new List<FileRecord>();
        await using (var dataCmd = connection.CreateCommand())
        {
            dataCmd.CommandText = isKeysetPaging && query.Offset == 0
                ? $"SELECT * FROM Files {whereClause} ORDER BY {sortColumn} {sortDirection} LIMIT @Limit;"
                : $"SELECT * FROM Files {whereClause} ORDER BY {sortColumn} {sortDirection} LIMIT @Limit OFFSET @Offset;";

            foreach (var p in parameters)
            {
                dataCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            dataCmd.Parameters.AddWithValue("@Limit", pageSize);
            if (!isKeysetPaging || query.Offset > 0)
            {
                dataCmd.Parameters.AddWithValue("@Offset", offset);
            }

            await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(FileRecordMapper.Map(reader));
            }
        }

        return new PagedResult<FileRecord>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = isKeysetPaging ? 1 : ((offset / pageSize) + 1),
            PageSize = pageSize
        };
    }

    public async Task<IReadOnlyList<string>> GetDiscoveredExtensionsAsync(Guid? scanRunId, CancellationToken cancellationToken = default)
    {
        var extensions = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (scanRunId.HasValue)
        {
            cmd.CommandText = "SELECT DISTINCT Extension FROM Files WHERE ScanRunId = @ScanRunId AND Extension != '' ORDER BY Extension ASC;";
            cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT DISTINCT Extension FROM Files WHERE Extension != '' ORDER BY Extension ASC;";
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                extensions.Add(reader.GetString(0));
            }
        }

        return extensions;
    }

    public async Task BackupDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullSource = Path.GetFullPath(_initializer.DatabasePath);
        var fullDest = Path.GetFullPath(destinationPath);
        if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup destination path cannot be the same file as the active database.");
        }

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var sourceConn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var destConnString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        }.ToString();

        await using var destConn = new SqliteConnection(destConnString);
        await destConn.OpenAsync(cancellationToken).ConfigureAwait(false);

        sourceConn.BackupDatabase(destConn);
        _logger.LogInformation("Database successfully backed up online to {DestinationPath}", destinationPath);
    }

    public async Task InsertAiReportAsync(AiReportRecord report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            if (report.Id > 0)
            {
                await using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE AiReports
                    SET Status = @Status,
                        ReportPath = @ReportPath,
                        ErrorMessage = @ErrorMessage
                    WHERE Id = @Id;
                ";
                updateCmd.Parameters.AddWithValue("@Id", report.Id);
                updateCmd.Parameters.AddWithValue("@Status", report.Status.ToString());
                updateCmd.Parameters.AddWithValue("@ReportPath", (object?)report.ReportPath ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@ErrorMessage", (object?)report.ErrorMessage ?? DBNull.Value);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AiReports (ScanRunId, ModelName, PromptVersion, Status, GeneratedAtUtc, ReportPath, ErrorMessage)
                VALUES (@ScanRunId, @ModelName, @PromptVersion, @Status, @GeneratedAtUtc, @ReportPath, @ErrorMessage);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@ScanRunId", report.ScanRunId.ToString());
            cmd.Parameters.AddWithValue("@ModelName", report.ModelName);
            cmd.Parameters.AddWithValue("@PromptVersion", report.PromptVersion);
            cmd.Parameters.AddWithValue("@Status", report.Status.ToString());
            cmd.Parameters.AddWithValue("@GeneratedAtUtc", report.GeneratedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@ReportPath", (object?)report.ReportPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)report.ErrorMessage ?? DBNull.Value);

            var id = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            report.Id = Convert.ToInt64(id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<AiReportRecord>> GetAiReportsAsync(Guid? scanRunId = null, CancellationToken cancellationToken = default)
    {
        var reports = new List<AiReportRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (scanRunId.HasValue)
        {
            cmd.CommandText = "SELECT * FROM AiReports WHERE ScanRunId = @ScanRunId ORDER BY GeneratedAtUtc DESC;";
            cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT * FROM AiReports ORDER BY GeneratedAtUtc DESC;";
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reports.Add(new AiReportRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
                ModelName = reader.GetString(reader.GetOrdinal("ModelName")),
                PromptVersion = reader.GetString(reader.GetOrdinal("PromptVersion")),
                Status = Enum.Parse<AiReportState>(reader.GetString(reader.GetOrdinal("Status")), ignoreCase: true),
                GeneratedAtUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("GeneratedAtUtc"))),
                ReportPath = reader.IsDBNull(reader.GetOrdinal("ReportPath")) ? null : reader.GetString(reader.GetOrdinal("ReportPath")),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage"))
            });
        }

        return reports;
    }

    public async Task UpdateFileHashBatchAsync(
        IEnumerable<FileHashUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var list = updates.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE Files
                SET HashAlgorithm = @HashAlgorithm,
                    HashValue = @HashValue
                WHERE Id = @Id;
            ";
            var pId = cmd.Parameters.Add("@Id", SqliteType.Integer);
            var pAlgorithm = cmd.Parameters.Add("@HashAlgorithm", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@HashValue", SqliteType.Text);

            foreach (var u in list)
            {
                pId.Value = u.FileId;
                pAlgorithm.Value = u.HashAlgorithm;
                pValue.Value = u.HashValue;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task UpdateFileHashAsync(long fileId, string hashAlgorithm, string hashValue, CancellationToken cancellationToken = default)
    {
        return UpdateFileHashBatchAsync(new[] { new FileHashUpdate(fileId, hashAlgorithm, hashValue) }, cancellationToken);
    }

    public async Task<string?> FindExistingFileHashAsync(string fullPath, long sizeBytes, DateTime? lastWriteTimeUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!lastWriteTimeUtc.HasValue) return null;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT HashValue, LastWriteTimeUtc FROM Files
            WHERE FullPath = @FullPath
              AND SizeBytes = @SizeBytes
              AND HashAlgorithm = 'SHA-256'
              AND HashValue IS NOT NULL
              AND HashValue != ''
            ORDER BY Id DESC
            LIMIT 5;
        ";
        cmd.Parameters.AddWithValue("@FullPath", fullPath);
        cmd.Parameters.AddWithValue("@SizeBytes", sizeBytes);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                var timeStr = reader.GetString(1);
                if (DateTime.TryParse(timeStr, out var parsedUtc))
                {
                    if (Math.Abs((parsedUtc.ToUniversalTime() - lastWriteTimeUtc.Value.ToUniversalTime()).TotalSeconds) <= 2)
                    {
                        return hash;
                    }
                }
            }
        }

        return null;
    }

    public async Task<FileRecord?> GetFileByIdAsync(long fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE Id = @Id LIMIT 1;";
        cmd.Parameters.AddWithValue("@Id", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return MapFileRecord(reader);
        }

        return null;
    }

    public async Task<RunComparisonResult> CompareScanRunsAsync(Guid baselineRunId, Guid targetRunId, CancellationToken cancellationToken = default)
    {
        var baselineRun = await GetScanRunAsync(baselineRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Baseline scan run {baselineRunId} was not found.");
        var targetRun = await GetScanRunAsync(targetRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Target scan run {targetRunId} was not found.");

        var result = new RunComparisonResult
        {
            BaselineRunId = baselineRunId,
            TargetRunId = targetRunId,
            BaselineTimeUtc = baselineRun.StartTimeUtc,
            TargetTimeUtc = targetRun.StartTimeUtc
        };

        if (!string.Equals(baselineRun.SelectedDrives, targetRun.SelectedDrives, StringComparison.OrdinalIgnoreCase))
        {
            result.WarningMessage = $"Notice: Baseline scan roots ({baselineRun.SelectedDrives}) differ from Target scan roots ({targetRun.SelectedDrives}). New/Deleted differences may reflect differing scan scopes rather than file operations.";
            _logger.LogWarning("Comparing runs with mismatched scan roots: Baseline={BaselineRoots}, Target={TargetRoots}", baselineRun.SelectedDrives, targetRun.SelectedDrives);
        }

        var extDict = new Dictionary<string, ExtensionDelta>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = @"
            -- Deleted files (in Baseline, not in Target)
            SELECT a.FullPath, a.Extension, a.SizeBytes AS BaselineSizeBytes, 0 AS TargetSizeBytes,
                   a.LastWriteTimeUtc AS BaselineModifiedUtc, NULL AS TargetModifiedUtc,
                   'Deleted' AS DeltaType
            FROM Files a
            LEFT JOIN Files b ON b.ScanRunId = @TargetRunId AND b.FullPath = a.FullPath COLLATE NOCASE
            WHERE a.ScanRunId = @BaselineRunId AND b.Id IS NULL

            UNION ALL

            -- New files (in Target, not in Baseline)
            SELECT b.FullPath, b.Extension, 0 AS BaselineSizeBytes, b.SizeBytes AS TargetSizeBytes,
                   NULL AS BaselineModifiedUtc, b.LastWriteTimeUtc AS TargetModifiedUtc,
                   'New' AS DeltaType
            FROM Files b
            LEFT JOIN Files a ON a.ScanRunId = @BaselineRunId AND a.FullPath = b.FullPath COLLATE NOCASE
            WHERE b.ScanRunId = @TargetRunId AND a.Id IS NULL

            UNION ALL

            -- Modified files (in both, but size or timestamp changed)
            SELECT b.FullPath, b.Extension, a.SizeBytes AS BaselineSizeBytes, b.SizeBytes AS TargetSizeBytes,
                   a.LastWriteTimeUtc AS BaselineModifiedUtc, b.LastWriteTimeUtc AS TargetModifiedUtc,
                   'Modified' AS DeltaType
            FROM Files a
            INNER JOIN Files b ON b.ScanRunId = @TargetRunId AND b.FullPath = a.FullPath COLLATE NOCASE
            WHERE a.ScanRunId = @BaselineRunId
              AND (a.SizeBytes != b.SizeBytes OR a.LastWriteTimeUtc IS NOT b.LastWriteTimeUtc)
            ORDER BY 1 ASC;
        ";
        cmd.Parameters.AddWithValue("@BaselineRunId", baselineRunId.ToString());
        cmd.Parameters.AddWithValue("@TargetRunId", targetRunId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fullPath = reader.GetString(0);
            var extension = reader.GetString(1);
            var baselineBytes = reader.GetInt64(2);
            var targetBytes = reader.GetInt64(3);
            DateTime? baseMod = reader.IsDBNull(4) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(4));
            DateTime? targetMod = reader.IsDBNull(5) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(5));
            var deltaTypeStr = reader.GetString(6);
            var deltaType = Enum.Parse<DeltaType>(deltaTypeStr, ignoreCase: true);

            var deltaRecord = new FileDeltaRecord
            {
                FullPath = fullPath,
                Extension = extension,
                DeltaType = deltaType,
                BaselineSizeBytes = baselineBytes,
                TargetSizeBytes = targetBytes,
                BaselineModifiedUtc = baseMod,
                TargetModifiedUtc = targetMod
            };
            result.Deltas.Add(deltaRecord);

            if (!extDict.TryGetValue(extension, out var extDelta))
            {
                extDelta = new ExtensionDelta { Extension = extension };
                extDict[extension] = extDelta;
            }

            extDelta.BaselineTotalBytes += baselineBytes;
            extDelta.TargetTotalBytes += targetBytes;

            switch (deltaType)
            {
                case DeltaType.New:
                    result.NewFilesCount++;
                    result.NewFilesBytes += targetBytes;
                    extDelta.NewCount++;
                    break;
                case DeltaType.Deleted:
                    result.DeletedFilesCount++;
                    result.DeletedFilesBytes += baselineBytes;
                    extDelta.DeletedCount++;
                    break;
                case DeltaType.Modified:
                    result.ModifiedFilesCount++;
                    extDelta.ModifiedCount++;
                    break;
            }
        }

        result.NetStorageDeltaBytes = result.Deltas.Sum(d => d.SizeDeltaBytes);
        result.ExtensionDeltas = extDict.Values.OrderByDescending(e => Math.Abs(e.NetDeltaBytes)).ToList();

        return result;
    }

    public async Task ExportComparisonCsvAsync(RunComparisonResult comparison, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".staging_compare_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
            {
                await writer.WriteLineAsync("DeltaType,FullPath,Extension,BaselineSizeBytes,TargetSizeBytes,SizeDeltaBytes,BaselineModifiedUtc,TargetModifiedUtc");
                foreach (var delta in comparison.Deltas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7}",
                        CsvSanitizer.EscapeCsv(delta.DeltaType.ToString()),
                        CsvSanitizer.EscapeCsv(delta.FullPath),
                        CsvSanitizer.EscapeCsv(delta.Extension),
                        delta.BaselineSizeBytes,
                        delta.TargetSizeBytes,
                        delta.SizeDeltaBytes,
                        delta.BaselineModifiedUtc?.ToString("o") ?? string.Empty,
                        delta.TargetModifiedUtc?.ToString("o") ?? string.Empty);

                    await writer.WriteLineAsync(line);
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
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

    public async Task InsertChatMessageAsync(ChatMessageRecord message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ChatMessages (ScanRunId, Role, Content, TimestampUtc, ContextCandidateIds)
                VALUES (@ScanRunId, @Role, @Content, @TimestampUtc, @ContextCandidateIds);
                SELECT last_insert_rowid();
            ";
            cmd.Parameters.AddWithValue("@ScanRunId", message.ScanRunId.ToString());
            cmd.Parameters.AddWithValue("@Role", message.Role);
            cmd.Parameters.AddWithValue("@Content", message.Content);
            cmd.Parameters.AddWithValue("@TimestampUtc", message.TimestampUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@ContextCandidateIds", (object?)message.ContextCandidateIds ?? DBNull.Value);

            var id = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            message.Id = Convert.ToInt64(id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> GetChatMessagesAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessageRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ChatMessages WHERE ScanRunId = @ScanRunId ORDER BY TimestampUtc ASC, Id ASC;";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new ChatMessageRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
                Role = reader.GetString(reader.GetOrdinal("Role")),
                Content = reader.GetString(reader.GetOrdinal("Content")),
                TimestampUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("TimestampUtc"))),
                ContextCandidateIds = reader.IsDBNull(reader.GetOrdinal("ContextCandidateIds")) ? null : reader.GetString(reader.GetOrdinal("ContextCandidateIds"))
            });
        }

        return messages;
    }

    public async Task DeleteChatMessagesAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ChatMessages WHERE ScanRunId = @ScanRunId;";
            cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static FileRecord MapFileRecord(SqliteDataReader reader) => FileRecordMapper.Map(reader);

    private static ScanRun MapScanRun(SqliteDataReader reader)
    {
        return new ScanRun
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            StartTimeUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("StartTimeUtc"))),
            EndTimeUtc = reader.IsDBNull(reader.GetOrdinal("EndTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("EndTimeUtc"))),
            Status = Enum.TryParse<ScanState>(reader.GetString(reader.GetOrdinal("Status")), ignoreCase: true, out var scanStatus) ? scanStatus : ScanState.Failed,
            SelectedDrives = reader.GetString(reader.GetOrdinal("SelectedDrives")),
            ConfigurationSnapshot = reader.GetString(reader.GetOrdinal("ConfigurationSnapshot")),
            FilesDiscovered = reader.GetInt64(reader.GetOrdinal("FilesDiscovered")),
            DirectoriesCompleted = reader.GetInt64(reader.GetOrdinal("DirectoriesCompleted")),
            ErrorCount = reader.GetInt64(reader.GetOrdinal("ErrorCount")),
            ApplicationVersion = reader.GetString(reader.GetOrdinal("ApplicationVersion"))
        };
    }

    private static DirectoryRecord MapDirectoryRecord(SqliteDataReader reader)
    {
        return new DirectoryRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
            Path = reader.GetString(reader.GetOrdinal("Path")),
            ParentPath = reader.IsDBNull(reader.GetOrdinal("ParentPath")) ? null : reader.GetString(reader.GetOrdinal("ParentPath")),
            Status = Enum.TryParse<DirectoryState>(reader.GetString(reader.GetOrdinal("Status")), ignoreCase: true, out var dirStatus) ? dirStatus : DirectoryState.Pending,
            FilesProcessed = reader.GetInt32(reader.GetOrdinal("FilesProcessed")),
            LastCheckpointUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("LastCheckpointUtc"))),
            LastError = reader.IsDBNull(reader.GetOrdinal("LastError")) ? null : reader.GetString(reader.GetOrdinal("LastError"))
        };
    }

    private static string EscapeLike(string value)
    {
        return value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }

    public async Task<IReadOnlyList<CandidateClassification>> GetCachedAnalysisResultsAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        var list = new List<CandidateClassification>();
        var seenFileIds = new HashSet<long>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ar.FileId, ar.Category, ar.RuleId, ar.Priority, ar.Confidence, ar.Explanation,
                   f.FullPath, f.FileName, f.Extension, f.SizeBytes, f.LastWriteTimeUtc
            FROM AnalysisResults ar
            LEFT JOIN Files f ON f.Id = ar.FileId
            WHERE ar.ScanRunId = @ScanRunId
            ORDER BY ar.Priority ASC, f.SizeBytes DESC;
        ";
        cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fileId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
            if (fileId > 0 && !seenFileIds.Add(fileId))
            {
                continue;
            }

            var categoryStr = reader.GetString(1);
            var ruleId = reader.GetString(2);
            var priority = reader.GetInt32(3);
            var confidence = reader.GetDouble(4);
            var explanation = reader.GetString(5);
            var fullPath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var fileName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var extension = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            var sizeBytes = reader.IsDBNull(9) ? 0L : reader.GetInt64(9);
            var lastWrite = reader.IsDBNull(10) ? (DateTime?)null : UtcDateTimeHelper.ParseUtc(reader.GetString(10));

            Enum.TryParse<CandidateCategory>(categoryStr, out var cat);

            list.Add(new CandidateClassification
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

        return list;
    }

    public async Task SaveAnalysisResultsAsync(Guid scanRunId, IEnumerable<CandidateClassification> results, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var delCmd = connection.CreateCommand())
            {
                delCmd.Transaction = (SqliteTransaction)tx;
                delCmd.CommandText = "DELETE FROM AnalysisResults WHERE ScanRunId = @ScanRunId;";
                delCmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
                await delCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = @"
                INSERT INTO AnalysisResults (ScanRunId, FileId, Category, RuleId, Priority, Confidence, Explanation, AnalysisVersion)
                VALUES (@ScanRunId, @FileId, @Category, @RuleId, @Priority, @Confidence, @Explanation, @AnalysisVersion);
            ";
            var pScan = cmd.Parameters.Add("@ScanRunId", SqliteType.Text);
            var pFile = cmd.Parameters.Add("@FileId", SqliteType.Integer);
            var pCat = cmd.Parameters.Add("@Category", SqliteType.Text);
            var pRule = cmd.Parameters.Add("@RuleId", SqliteType.Text);
            var pPrio = cmd.Parameters.Add("@Priority", SqliteType.Integer);
            var pConf = cmd.Parameters.Add("@Confidence", SqliteType.Real);
            var pExp = cmd.Parameters.Add("@Explanation", SqliteType.Text);
            var pVer = cmd.Parameters.Add("@AnalysisVersion", SqliteType.Text);

            var distinctResults = results.GroupBy(c => c.FileRecordId).Select(g => g.First()).ToList();

            foreach (var c in distinctResults)
            {
                pScan.Value = scanRunId.ToString();
                pFile.Value = c.FileRecordId > 0 ? c.FileRecordId : (object)DBNull.Value;
                pCat.Value = c.Category.ToString();
                pRule.Value = c.RuleId;
                pPrio.Value = c.Priority;
                pConf.Value = c.Confidence;
                pExp.Value = c.Explanation;
                pVer.Value = "1.3";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAnalysisResultsAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AnalysisResults WHERE ScanRunId = @ScanRunId;";
            cmd.Parameters.AddWithValue("@ScanRunId", scanRunId.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<StorageMetricsSummary?> GetScanMetricsAsync(Guid scanRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MetricsJson FROM ScanMetrics WHERE ScanRunId = @Id LIMIT 1;";
        cmd.Parameters.AddWithValue("@Id", scanRunId.ToString());
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<StorageMetricsSummary>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached scan metrics for {ScanRunId}", scanRunId);
            }
        }
        return null;
    }

    public async Task SaveScanMetricsAsync(Guid scanRunId, StorageMetricsSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ScanMetrics (ScanRunId, MetricsJson, CalculatedAtUtc)
                VALUES (@Id, @Json, @CalculatedAt)
                ON CONFLICT(ScanRunId) DO UPDATE SET
                    MetricsJson = excluded.MetricsJson,
                    CalculatedAtUtc = excluded.CalculatedAtUtc;
            ";
            cmd.Parameters.AddWithValue("@Id", scanRunId.ToString());
            cmd.Parameters.AddWithValue("@Json", json);
            cmd.Parameters.AddWithValue("@CalculatedAt", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CheckpointTruncateAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Explicit WAL checkpoint TRUNCATE skipped or failed.");
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
