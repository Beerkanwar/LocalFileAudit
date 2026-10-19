using System.Data;
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
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = connection.BeginTransaction();

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM Files WHERE ScanRunId = @Id;
                DELETE FROM Directories WHERE ScanRunId = @Id;
                DELETE FROM ScanErrors WHERE ScanRunId = @Id;
                DELETE FROM ScanRuns WHERE Id = @Id;
            ";
            cmd.Parameters.AddWithValue("@Id", scanRunId.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Discarded scan run {ScanRunId} from local database.", scanRunId);
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
                VALUES (@ScanRunId, @Path, @ParentPath, @Status, @FilesProcessed, @LastCheckpointUtc, @LastError);
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

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateDirectoryStatusAsync(
        long directoryId,
        DirectoryState status,
        int filesProcessed,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE Directories
                SET Status = @Status,
                    FilesProcessed = @FilesProcessed,
                    LastCheckpointUtc = @LastCheckpointUtc,
                    LastError = @LastError
                WHERE Id = @Id;
            ";
            cmd.Parameters.AddWithValue("@Id", directoryId);
            cmd.Parameters.AddWithValue("@Status", status.ToString());
            cmd.Parameters.AddWithValue("@FilesProcessed", filesProcessed);
            cmd.Parameters.AddWithValue("@LastCheckpointUtc", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@LastError", (object?)lastError ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
                TimestampUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("TimestampUtc")))
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
            whereConditions.Add("(FileName LIKE @SearchText OR FullPath LIKE @SearchText)");
            parameters.Add(new SqliteParameter("@SearchText", $"%{query.SearchText.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Extension))
        {
            var ext = query.Extension.Trim();
            if (!ext.StartsWith('.')) ext = "." + ext;
            whereConditions.Add("Extension = @Extension COLLATE NOCASE");
            parameters.Add(new SqliteParameter("@Extension", ext));
        }

        if (!string.IsNullOrWhiteSpace(query.DriveOrPrefix))
        {
            whereConditions.Add("FullPath LIKE @Prefix");
            parameters.Add(new SqliteParameter("@Prefix", $"{query.DriveOrPrefix.Trim()}%"));
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

        var sortColumn = query.SortBy?.ToLowerInvariant() switch
        {
            "filename" => "FileName",
            "extension" => "Extension",
            "lastwritetimeutc" => "LastWriteTimeUtc",
            "fullpath" => "FullPath",
            _ => "SizeBytes"
        };
        var sortDirection = query.SortDescending ? "DESC" : "ASC";

        var pageSize = Math.Clamp(query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);

        var items = new List<FileRecord>();
        await using (var dataCmd = connection.CreateCommand())
        {
            dataCmd.CommandText = $"SELECT * FROM Files {whereClause} ORDER BY {sortColumn} {sortDirection} LIMIT @Limit OFFSET @Offset;";
            foreach (var p in parameters)
            {
                dataCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            dataCmd.Parameters.AddWithValue("@Limit", pageSize);
            dataCmd.Parameters.AddWithValue("@Offset", offset);

            await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(MapFileRecord(reader));
            }
        }

        return new PagedResult<FileRecord>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = (offset / pageSize) + 1,
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
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var sourceConn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var destConnString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            await using var destConn = new SqliteConnection(destConnString);
            await destConn.OpenAsync(cancellationToken).ConfigureAwait(false);

            sourceConn.BackupDatabase(destConn);
            _logger.LogInformation("Database successfully backed up online to {DestinationPath}", destinationPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertAiReportAsync(AiReportRecord report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                GeneratedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("GeneratedAtUtc"))),
                ReportPath = reader.IsDBNull(reader.GetOrdinal("ReportPath")) ? null : reader.GetString(reader.GetOrdinal("ReportPath")),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage"))
            });
        }

        return reports;
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

    private static ScanRun MapScanRun(SqliteDataReader reader)
    {
        return new ScanRun
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            StartTimeUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartTimeUtc"))),
            EndTimeUtc = reader.IsDBNull(reader.GetOrdinal("EndTimeUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("EndTimeUtc"))),
            Status = Enum.Parse<ScanState>(reader.GetString(reader.GetOrdinal("Status")), ignoreCase: true),
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
            Status = Enum.Parse<DirectoryState>(reader.GetString(reader.GetOrdinal("Status")), ignoreCase: true),
            FilesProcessed = reader.GetInt32(reader.GetOrdinal("FilesProcessed")),
            LastCheckpointUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastCheckpointUtc"))),
            LastError = reader.IsDBNull(reader.GetOrdinal("LastError")) ? null : reader.GetString(reader.GetOrdinal("LastError"))
        };
    }
}
