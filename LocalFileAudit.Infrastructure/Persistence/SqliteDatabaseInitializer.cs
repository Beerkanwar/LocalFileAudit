using LocalFileAudit.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Persistence;

/// <summary>
/// Initializes SQLite schema, applies indexes, and enables WAL mode.
/// </summary>
public sealed class SqliteDatabaseInitializer : IDatabaseInitializer
{
    private readonly IAppEnvironment _environment;
    private readonly ILogger<SqliteDatabaseInitializer> _logger;
    private readonly string _databasePath;

    public string DatabasePath => _databasePath;

    public SqliteDatabaseInitializer(IAppEnvironment environment, ILogger<SqliteDatabaseInitializer> logger)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _databasePath = Path.Combine(_environment.DatabaseDirectory, "inventory.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _environment.EnsureDirectoriesCreated();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _logger.LogInformation("Initializing SQLite inventory database at {Path}", _databasePath);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL mode & pragmas
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
            ";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Schema creation
        var ddl = @"
            CREATE TABLE IF NOT EXISTS ScanRuns (
                Id TEXT PRIMARY KEY,
                StartTimeUtc TEXT NOT NULL,
                EndTimeUtc TEXT,
                Status TEXT NOT NULL,
                SelectedDrives TEXT,
                ConfigurationSnapshot TEXT,
                FilesDiscovered INTEGER NOT NULL DEFAULT 0,
                DirectoriesCompleted INTEGER NOT NULL DEFAULT 0,
                ErrorCount INTEGER NOT NULL DEFAULT 0,
                ApplicationVersion TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Directories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScanRunId TEXT NOT NULL,
                Path TEXT NOT NULL,
                ParentPath TEXT,
                Status TEXT NOT NULL,
                FilesProcessed INTEGER NOT NULL DEFAULT 0,
                LastCheckpointUtc TEXT NOT NULL,
                LastError TEXT,
                FOREIGN KEY (ScanRunId) REFERENCES ScanRuns(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScanRunId TEXT NOT NULL,
                FullPath TEXT NOT NULL,
                ParentPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                CreationTimeUtc TEXT,
                LastWriteTimeUtc TEXT,
                LastAccessTimeUtc TEXT,
                Attributes INTEGER NOT NULL,
                ObservedAtUtc TEXT NOT NULL,
                MetadataStatus TEXT NOT NULL,
                HashAlgorithm TEXT,
                HashValue TEXT,
                UNIQUE(ScanRunId, FullPath),
                FOREIGN KEY (ScanRunId) REFERENCES ScanRuns(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ScanErrors (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScanRunId TEXT NOT NULL,
                Path TEXT NOT NULL,
                Operation TEXT NOT NULL,
                ErrorCode TEXT,
                ErrorMessage TEXT NOT NULL,
                TimestampUtc TEXT NOT NULL,
                FOREIGN KEY (ScanRunId) REFERENCES ScanRuns(Id) ON DELETE CASCADE
            );

            -- Indexes for high performance querying
            CREATE INDEX IF NOT EXISTS idx_files_scan_path ON Files(ScanRunId, FullPath);
            CREATE INDEX IF NOT EXISTS idx_files_scan_parent ON Files(ScanRunId, ParentPath);
            CREATE INDEX IF NOT EXISTS idx_files_scan_ext ON Files(ScanRunId, Extension);
            CREATE INDEX IF NOT EXISTS idx_files_scan_size ON Files(ScanRunId, SizeBytes);
            CREATE INDEX IF NOT EXISTS idx_dirs_scan_path ON Directories(ScanRunId, Path);
            CREATE INDEX IF NOT EXISTS idx_dirs_scan_status ON Directories(ScanRunId, Status);
            CREATE INDEX IF NOT EXISTS idx_errors_scan ON ScanErrors(ScanRunId);
        ";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Database schema and indexes verified.");
    }
}
