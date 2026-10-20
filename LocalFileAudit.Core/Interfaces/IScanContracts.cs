using LocalFileAudit.Core.Models;

namespace LocalFileAudit.Core.Interfaces;

/// <summary>
/// Initializes and migrates the SQLite inventory database.
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    string DatabasePath { get; }
}

/// <summary>
/// Crash-safe repository for storing scan runs, directories, files, and errors.
/// </summary>
public interface IScanRepository
{
    Task CreateScanRunAsync(ScanRun run, CancellationToken cancellationToken = default);
    Task UpdateScanRunStatusAsync(Guid scanRunId, ScanState state, DateTime? endTimeUtc, long filesDiscovered, long dirsCompleted, long errorCount, CancellationToken cancellationToken = default);
    Task<ScanRun?> GetScanRunAsync(Guid scanRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanRun>> GetRecentScanRunsAsync(int count = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanRun>> GetIncompleteScanRunsAsync(CancellationToken cancellationToken = default);
    Task DiscardScanRunAsync(Guid scanRunId, CancellationToken cancellationToken = default);

    Task InsertDirectoryBatchAsync(IEnumerable<DirectoryRecord> directories, CancellationToken cancellationToken = default);
    Task UpdateDirectoryStatusAsync(long directoryId, DirectoryState status, int filesProcessed, string? lastError, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryRecord>> GetPendingOrInterruptedDirectoriesAsync(Guid scanRunId, CancellationToken cancellationToken = default);

    Task InsertFileBatchAsync(IEnumerable<FileRecord> files, CancellationToken cancellationToken = default);
    Task<long> GetFileCountForScanAsync(Guid scanRunId, CancellationToken cancellationToken = default);
    Task<long> GetTotalLogicalSizeBytesForScanAsync(Guid scanRunId, CancellationToken cancellationToken = default);

    Task InsertScanErrorAsync(ScanErrorRecord error, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanErrorRecord>> GetScanErrorsAsync(Guid scanRunId, int limit = 100, CancellationToken cancellationToken = default);

    Task<PagedResult<FileRecord>> QueryFilesAsync(FileInventoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetDiscoveredExtensionsAsync(Guid? scanRunId, CancellationToken cancellationToken = default);
    Task BackupDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task InsertAiReportAsync(AiReportRecord report, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiReportRecord>> GetAiReportsAsync(Guid? scanRunId = null, CancellationToken cancellationToken = default);

    Task UpdateFileHashAsync(long fileId, string hashAlgorithm, string hashValue, CancellationToken cancellationToken = default);
    Task<string?> FindExistingFileHashAsync(string fullPath, long sizeBytes, DateTime? lastWriteTimeUtc, CancellationToken cancellationToken = default);
    Task<FileRecord?> GetFileByIdAsync(long fileId, CancellationToken cancellationToken = default);
    Task<RunComparisonResult> CompareScanRunsAsync(Guid baselineRunId, Guid targetRunId, CancellationToken cancellationToken = default);
    Task ExportComparisonCsvAsync(RunComparisonResult comparison, string destinationPath, CancellationToken cancellationToken = default);
    Task InsertChatMessageAsync(ChatMessageRecord message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessageRecord>> GetChatMessagesAsync(Guid scanRunId, CancellationToken cancellationToken = default);
    Task DeleteChatMessagesAsync(Guid scanRunId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Core filesystem scanner engine.
/// Traverses directories asynchronously with bounded concurrency, backpressure, and cancellation.
/// </summary>
public interface IScanEngine
{
    Task<ScanRun> StartScanAsync(ScanTarget target, CancellationToken cancellationToken, IProgress<ScanProgress>? progress = null);
    Task<ScanRun> ResumeScanAsync(Guid scanRunId, CancellationToken cancellationToken, IProgress<ScanProgress>? progress = null);
    void PauseScan();
    void ResumeScanExecution();
    bool IsRunning { get; }
    bool IsPaused { get; }
    ScanState CurrentState { get; }
}
