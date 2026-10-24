using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Configuration;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.Infrastructure.Scanning;

/// <summary>
/// Safe, cancellable, resumable filesystem scanner.
/// Operates strictly read-only with bounded concurrency, backpressure, and crash-safe checkpoints.
/// </summary>
public sealed class FilesystemScanner : IScanEngine
{
    private abstract record PersistenceItem;
    private sealed record FilePersistenceItem(FileRecord File) : PersistenceItem;
    private sealed record DirectoryCheckpointItem(long DirectoryId, DirectoryState State, int FilesProcessed, string? LastError) : PersistenceItem;

    private readonly IScanRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<FilesystemScanner> _logger;

    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly object _stateLock = new();

    private bool _isRunning;
    private bool _isPaused;
    private ScanState _currentState = ScanState.Pending;

    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;
    public ScanState CurrentState => _currentState;

    public FilesystemScanner(
        IScanRepository repository,
        IConfigurationService configurationService,
        ILogger<FilesystemScanner> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void PauseScan()
    {
        lock (_stateLock)
        {
            if (_isRunning && !_isPaused)
            {
                _isPaused = true;
                _currentState = ScanState.Paused;
                _pauseEvent.Reset();
                _logger.LogInformation("Scan paused.");
            }
        }
    }

    public void ResumeScanExecution()
    {
        lock (_stateLock)
        {
            if (_isRunning && _isPaused)
            {
                _isPaused = false;
                _currentState = ScanState.InProgress;
                _pauseEvent.Set();
                _logger.LogInformation("Scan resumed.");
            }
        }
    }

    public async Task<ScanRun> StartScanAsync(ScanTarget target, CancellationToken cancellationToken, IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("A scan is already running.");
            }
            _isRunning = true;
            _isPaused = false;
            _currentState = ScanState.InProgress;
            _pauseEvent.Set();
        }

        ScanRun? run = null;
        try
        {
            var config = _configurationService.Current;
            var targets = ResolveTargetRoots(target);

            run = new ScanRun
            {
                Id = Guid.NewGuid(),
                StartTimeUtc = DateTime.UtcNow,
                Status = ScanState.InProgress,
                SelectedDrives = SelectedRootsHelper.JoinRoots(targets),
                ConfigurationSnapshot = JsonSerializer.Serialize(config),
                ApplicationVersion = LocalFileAudit.Core.Common.AppVersion.Current
            };

            _logger.LogInformation("Starting new scan {ScanRunId} targeting roots: {Roots}", run.Id, run.SelectedDrives);

            if (cancellationToken.IsCancellationRequested)
            {
                run.Status = ScanState.Cancelled;
                run.EndTimeUtc = DateTime.UtcNow;
                await _repository.CreateScanRunAsync(run, CancellationToken.None).ConfigureAwait(false);
                return run;
            }

            await _repository.CreateScanRunAsync(run, CancellationToken.None).ConfigureAwait(false);

            // Initial root directories
            var rootDirRecords = targets.Select(t => new DirectoryRecord
            {
                ScanRunId = run.Id,
                Path = t,
                ParentPath = null,
                Status = DirectoryState.Pending,
                LastCheckpointUtc = DateTime.UtcNow
            }).ToList();

            await _repository.InsertDirectoryBatchAsync(rootDirRecords, CancellationToken.None).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                run.Status = ScanState.Cancelled;
                run.EndTimeUtc = DateTime.UtcNow;
                await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Cancelled, run.EndTimeUtc, 0, 0, 0, CancellationToken.None).ConfigureAwait(false);
                return run;
            }

            return await ExecuteScanLoopAsync(run, rootDirRecords, target, cancellationToken, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (run != null)
            {
                _logger.LogWarning("Scan {ScanRunId} was cancelled during initialization.", run.Id);
                run.Status = ScanState.Cancelled;
                run.EndTimeUtc = DateTime.UtcNow;
                await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Cancelled, run.EndTimeUtc, run.FilesDiscovered, run.DirectoriesCompleted, run.ErrorCount, CancellationToken.None).ConfigureAwait(false);
                return run;
            }
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _isPaused = false;
                if (run != null)
                {
                    _currentState = run.Status;
                }
            }
        }
    }

    public async Task<ScanRun> ResumeScanAsync(Guid scanRunId, CancellationToken cancellationToken, IProgress<ScanProgress>? progress = null)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("A scan is already running.");
            }
            _isRunning = true;
            _isPaused = false;
            _currentState = ScanState.InProgress;
            _pauseEvent.Set();
        }

        ScanRun? run = null;
        try
        {
            run = await _repository.GetScanRunAsync(scanRunId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new ArgumentException($"Scan run {scanRunId} not found.", nameof(scanRunId));

            if (cancellationToken.IsCancellationRequested)
            {
                run.Status = ScanState.Cancelled;
                run.EndTimeUtc = DateTime.UtcNow;
                await _repository.UpdateScanRunStatusAsync(scanRunId, ScanState.Cancelled, run.EndTimeUtc, run.FilesDiscovered, run.DirectoriesCompleted, run.ErrorCount, CancellationToken.None).ConfigureAwait(false);
                return run;
            }

            _logger.LogInformation("Resuming scan {ScanRunId}...", scanRunId);
            await _repository.UpdateScanRunStatusAsync(scanRunId, ScanState.InProgress, null, run.FilesDiscovered, run.DirectoriesCompleted, run.ErrorCount, CancellationToken.None).ConfigureAwait(false);

            var pendingDirs = await _repository.GetPendingOrInterruptedDirectoriesAsync(scanRunId, CancellationToken.None).ConfigureAwait(false);

            AppConfiguration? snapshotConfig = null;
            if (!string.IsNullOrWhiteSpace(run.ConfigurationSnapshot))
            {
                try
                {
                    snapshotConfig = JsonSerializer.Deserialize<AppConfiguration>(run.ConfigurationSnapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize configuration snapshot for scan {ScanRunId}; using current configuration.", scanRunId);
                }
            }

            var effectiveConfig = snapshotConfig ?? _configurationService.Current;
            var target = new ScanTarget
            {
                ExcludedPaths = effectiveConfig.Scanning.ExcludedPaths?.ToList() ?? new List<string>(),
                FollowReparsePoints = effectiveConfig.Scanning.FollowReparsePoints,
                LowImpactMode = effectiveConfig.Scanning.LowImpactMode,
                RecordCloudPlaceholders = effectiveConfig.Scanning.RecordCloudPlaceholders
            };

            return await ExecuteScanLoopAsync(run, pendingDirs, target, cancellationToken, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (run != null)
            {
                _logger.LogWarning("Resumed scan {ScanRunId} was cancelled during initialization.", run.Id);
                run.Status = ScanState.Cancelled;
                run.EndTimeUtc = DateTime.UtcNow;
                await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Cancelled, run.EndTimeUtc, run.FilesDiscovered, run.DirectoriesCompleted, run.ErrorCount, CancellationToken.None).ConfigureAwait(false);
                return run;
            }
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _isPaused = false;
                if (run != null)
                {
                    _currentState = run.Status;
                }
            }
        }
    }

    private async Task<ScanRun> ExecuteScanLoopAsync(
        ScanRun run,
        IEnumerable<DirectoryRecord> initialDirs,
        ScanTarget target,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        var config = _configurationService.Current;
        var batchSize = Math.Clamp(config.Scanning.DatabaseBatchSize, 10, 50000);
        var maxWorkers = Math.Clamp(config.Scanning.MaxConcurrentDirectoryWorkers, 1, 16);

        long filesDiscovered = run.FilesDiscovered;
        long dirsCompleted = run.DirectoriesCompleted;
        long errorCount = run.ErrorCount;

        var directoryQueue = new ConcurrentQueue<DirectoryRecord>(initialDirs);
        var persistenceChannel = Channel.CreateBounded<PersistenceItem>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        var stopwatch = Stopwatch.StartNew();
        var lastProgressReport = Stopwatch.StartNew();
        string currentDirectoryDisplay = string.Empty;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeWorkers = 0;
        Exception? persistenceFault = null;
        var visitedDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Background batch persistence consumer
        var persistenceTask = Task.Run(async () =>
        {
            try
            {
                var buffer = new List<FileRecord>(batchSize);

                async Task FlushBufferAsync()
                {
                    if (buffer.Count > 0)
                    {
                        await _repository.InsertFileBatchAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }

                while (await persistenceChannel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    while (persistenceChannel.Reader.TryRead(out var item))
                    {
                        if (item is FilePersistenceItem fileItem)
                        {
                            buffer.Add(fileItem.File);
                            if (buffer.Count >= batchSize)
                            {
                                await FlushBufferAsync().ConfigureAwait(false);
                            }
                        }
                        else if (item is DirectoryCheckpointItem dirItem)
                        {
                            // Ensure all files for directory are durable in SQLite BEFORE directory status is marked
                            await FlushBufferAsync().ConfigureAwait(false);
                            await _repository.UpdateDirectoryStatusAsync(dirItem.DirectoryId, dirItem.State, dirItem.FilesProcessed, dirItem.LastError, CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    await FlushBufferAsync().ConfigureAwait(false);
                }

                await FlushBufferAsync().ConfigureAwait(false);
            }
            catch (Exception pEx)
            {
                persistenceFault = pEx;
                _logger.LogError(pEx, "Batch persistence consumer faulted for scan {ScanRunId}. Cancelling workers.", run.Id);
                try { linkedCts.Cancel(); } catch { }
                throw;
            }
        });

        void ReportProgress(ScanState state, string? detail = null)
        {
            if (progress != null && (lastProgressReport.ElapsedMilliseconds > 100 || filesDiscovered <= 10 || state != ScanState.InProgress))
            {
                lastProgressReport.Restart();
                var elapsed = stopwatch.Elapsed;
                var rate = elapsed.TotalSeconds > 0 ? filesDiscovered / elapsed.TotalSeconds : 0;

                progress.Report(new ScanProgress
                {
                    ScanRunId = run.Id,
                    CurrentDirectory = currentDirectoryDisplay,
                    FilesDiscovered = filesDiscovered,
                    DirectoriesCompleted = dirsCompleted,
                    FilesProcessed = filesDiscovered,
                    ElapsedTime = elapsed,
                    FilesPerSecond = rate,
                    ErrorCount = errorCount,
                    State = state,
                    StatusDetail = detail
                });
            }
        }

        try
        {
            var workerTasks = Enumerable.Range(0, maxWorkers).Select(async workerId =>
            {
                while (!linkedCts.IsCancellationRequested)
                {
                    _pauseEvent.Wait(linkedCts.Token);

                    Interlocked.Increment(ref activeWorkers);
                    DirectoryRecord? dirRecord;
                    if (!directoryQueue.TryDequeue(out dirRecord))
                    {
                        var remaining = Interlocked.Decrement(ref activeWorkers);
                        if (remaining == 0 && directoryQueue.IsEmpty)
                        {
                            break;
                        }

                        await Task.Delay(15, linkedCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        currentDirectoryDisplay = dirRecord.Path;
                        await ProcessDirectoryAsync(
                            dirRecord,
                            run.Id,
                            target,
                            persistenceChannel.Writer,
                            directoryQueue,
                            () => Interlocked.Increment(ref filesDiscovered),
                            () => Interlocked.Increment(ref errorCount),
                            visitedDirectories,
                            linkedCts.Token).ConfigureAwait(false);

                        Interlocked.Increment(ref dirsCompleted);
                        ReportProgress(ScanState.InProgress);

                        if (target.LowImpactMode)
                        {
                            await Task.Delay(15, linkedCts.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeWorkers);
                    }
                }
            }).ToArray();

            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();

            persistenceChannel.Writer.Complete();
            await persistenceTask.ConfigureAwait(false);

            long durableFiles = filesDiscovered;
            long durableDirs = dirsCompleted;
            try
            {
                durableFiles = await _repository.GetFileCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                durableDirs = await _repository.GetCompletedDirectoryCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception countEx)
            {
                _logger.LogWarning(countEx, "Failed to derive exact file/directory count from database; using in-memory count.");
            }

            run.Status = ScanState.Completed;
            run.EndTimeUtc = DateTime.UtcNow;
            run.FilesDiscovered = durableFiles;
            run.DirectoriesCompleted = durableDirs;
            run.ErrorCount = errorCount;

            await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Completed, run.EndTimeUtc, durableFiles, durableDirs, errorCount, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Scan {ScanRunId} completed successfully. Files: {Files}, Dirs: {Dirs}, Errors: {Errors}", run.Id, durableFiles, durableDirs, errorCount);

            ReportProgress(ScanState.Completed, "Scan completed successfully.");
        }
        catch (OperationCanceledException)
        {
            persistenceChannel.Writer.TryComplete();
            try
            {
                await persistenceTask.ConfigureAwait(false);
            }
            catch (Exception pEx)
            {
                persistenceFault ??= pEx;
                _logger.LogWarning(pEx, "Persistence task finished with exception during cancellation for scan {ScanRunId}.", run.Id);
            }

            long durableFiles = filesDiscovered;
            long durableDirs = dirsCompleted;
            try
            {
                durableFiles = await _repository.GetFileCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                durableDirs = await _repository.GetCompletedDirectoryCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception countEx)
            {
                _logger.LogWarning(countEx, "Failed to derive exact count during cancellation.");
            }

            run.FilesDiscovered = durableFiles;
            run.DirectoriesCompleted = durableDirs;
            run.ErrorCount = errorCount;
            run.EndTimeUtc = DateTime.UtcNow;

            if (persistenceFault != null || (!cancellationToken.IsCancellationRequested && persistenceTask.IsFaulted))
            {
                var faultEx = persistenceFault ?? persistenceTask.Exception?.GetBaseException();
                _logger.LogError(faultEx, "Scan {ScanRunId} failed due to persistence consumer fault (e.g. disk full or schema error).", run.Id);
                run.Status = ScanState.Failed;

                try
                {
                    await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Failed, run.EndTimeUtc, durableFiles, durableDirs, errorCount, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to update scan run status to Failed in DB.");
                }
                ReportProgress(ScanState.Failed, $"Scan failed due to persistence error: {faultEx?.Message ?? "Database failure"}");
            }
            else
            {
                _logger.LogWarning("Scan {ScanRunId} was cancelled by user.", run.Id);
                run.Status = ScanState.Cancelled;

                try
                {
                    await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Cancelled, run.EndTimeUtc, durableFiles, durableDirs, errorCount, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to update scan run status to Cancelled in DB.");
                }
                ReportProgress(ScanState.Cancelled, "Scan cancelled. Committed batches preserved.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan {ScanRunId} encountered unexpected error.", run.Id);
            persistenceChannel.Writer.TryComplete();
            try
            {
                await persistenceTask.ConfigureAwait(false);
            }
            catch (Exception pEx)
            {
                _logger.LogWarning(pEx, "Persistence task finished with exception during failure handling for scan {ScanRunId}.", run.Id);
            }

            long durableFiles = filesDiscovered;
            long durableDirs = dirsCompleted;
            try
            {
                durableFiles = await _repository.GetFileCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                durableDirs = await _repository.GetCompletedDirectoryCountForScanAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception countEx)
            {
                _logger.LogWarning(countEx, "Failed to derive exact count during failure.");
            }

            run.Status = ScanState.Failed;
            run.EndTimeUtc = DateTime.UtcNow;
            run.FilesDiscovered = durableFiles;
            run.DirectoriesCompleted = durableDirs;
            run.ErrorCount = errorCount;

            try
            {
                await _repository.UpdateScanRunStatusAsync(run.Id, ScanState.Failed, run.EndTimeUtc, durableFiles, durableDirs, errorCount, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Failed to update scan run status to Failed in DB.");
            }
            ReportProgress(ScanState.Failed, $"Scan failed: {ex.Message}");
        }
        finally
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _isPaused = false;
                _currentState = run.Status;
            }
        }

        return run;
    }

    private async Task ProcessDirectoryAsync(
        DirectoryRecord dirRecord,
        Guid scanRunId,
        ScanTarget target,
        ChannelWriter<PersistenceItem> channelWriter,
        ConcurrentQueue<DirectoryRecord> queue,
        Action onFileDiscovered,
        Action onErrorEncountered,
        ConcurrentDictionary<string, byte> visitedDirectories,
        CancellationToken cancellationToken)
    {
        var path = dirRecord.Path;

        // Check if excluded with proper directory boundary semantics (prevent C:\Temp excluding C:\Temp2)
        if (target.ExcludedPaths.Any(p =>
        {
            var normP = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.Equals(normP, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(normP + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(normP + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }))
        {
            await _repository.UpdateDirectoryStatusAsync(dirRecord.Id, DirectoryState.Skipped, 0, "Excluded by configuration", cancellationToken).ConfigureAwait(false);
            return;
        }

        DirectoryInfo dirInfo;
        try
        {
            dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                await _repository.UpdateDirectoryStatusAsync(dirRecord.Id, DirectoryState.Error, 0, "Directory does not exist", cancellationToken).ConfigureAwait(false);
                return;
            }

            // Reparse point safety check: Symlinks/junctions
            if (!target.FollowReparsePoints && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogDebug("Skipping reparse point directory {Path}", path);
                await _repository.UpdateDirectoryStatusAsync(dirRecord.Id, DirectoryState.Skipped, 0, "Skipped reparse point / junction", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (target.FollowReparsePoints)
            {
                if (!visitedDirectories.TryAdd(dirInfo.FullName, 0))
                {
                    _logger.LogDebug("Skipping already visited directory (cycle prevention): {Path}", path);
                    await _repository.UpdateDirectoryStatusAsync(dirRecord.Id, DirectoryState.Skipped, 0, "Skipped already visited directory (cycle prevention)", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "DirectoryInfo", ex, cancellationToken).ConfigureAwait(false);
            await _repository.UpdateDirectoryStatusAsync(dirRecord.Id, DirectoryState.Error, 0, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var filesCount = 0;
        var hasErrors = false;
        string? errorDetail = null;

        // Enumerate files
        try
        {
            foreach (var fi in dirInfo.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isCloudPlaceholder = IsCloudPlaceholder(fi.Attributes);
                var isReparse = (fi.Attributes & FileAttributes.ReparsePoint) != 0;

                if (isCloudPlaceholder)
                {
                    if (target.RecordCloudPlaceholders)
                    {
                        var fileRecord = CreateFileRecord(fi, scanRunId, isCloudPlaceholder: true);
                        await channelWriter.WriteAsync(new FilePersistenceItem(fileRecord), cancellationToken).ConfigureAwait(false);
                        filesCount++;
                        onFileDiscovered();
                    }
                    continue;
                }

                if (isReparse && !target.FollowReparsePoints)
                {
                    _logger.LogDebug("Skipping reparse point file {Path}", fi.FullName);
                    continue;
                }

                var standardRecord = CreateFileRecord(fi, scanRunId, isCloudPlaceholder: false);
                await channelWriter.WriteAsync(new FilePersistenceItem(standardRecord), cancellationToken).ConfigureAwait(false);
                filesCount++;
                onFileDiscovered();
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            hasErrors = true;
            errorDetail ??= ex.Message;
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "EnumerateFiles", ex, cancellationToken).ConfigureAwait(false);
        }
        catch (PathTooLongException ex)
        {
            hasErrors = true;
            errorDetail ??= ex.Message;
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "EnumerateFiles", ex, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            hasErrors = true;
            errorDetail ??= ex.Message;
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "EnumerateFiles", ex, cancellationToken).ConfigureAwait(false);
        }

        // Enumerate subdirectories
        var newSubdirs = new List<DirectoryRecord>();
        try
        {
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip reparse points before enqueueing
                if (!target.FollowReparsePoints && (subDir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogDebug("Skipping reparse point child directory {Path}", subDir.FullName);
                    continue;
                }

                var subRecord = new DirectoryRecord
                {
                    ScanRunId = scanRunId,
                    Path = subDir.FullName,
                    ParentPath = path,
                    Status = DirectoryState.Pending,
                    LastCheckpointUtc = DateTime.UtcNow
                };
                newSubdirs.Add(subRecord);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            hasErrors = true;
            errorDetail ??= ex.Message;
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "EnumerateDirectories", ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            hasErrors = true;
            errorDetail ??= ex.Message;
            onErrorEncountered();
            await RecordErrorAsync(scanRunId, path, "EnumerateDirectories", ex, cancellationToken).ConfigureAwait(false);
        }

        if (newSubdirs.Count > 0)
        {
            await _repository.InsertDirectoryBatchAsync(newSubdirs, cancellationToken).ConfigureAwait(false);
            foreach (var sub in newSubdirs)
            {
                queue.Enqueue(sub);
            }
        }

        // Checkpoint directory completion through persistence channel ensuring all directory files are durable first
        var dirState = hasErrors ? DirectoryState.Error : DirectoryState.Completed;
        await channelWriter.WriteAsync(new DirectoryCheckpointItem(dirRecord.Id, dirState, filesCount, errorDetail), cancellationToken).ConfigureAwait(false);
    }

    public static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        const int RecallOnDataAccess = 0x00400000;
        const int RecallOnOpen = 0x00040000;
        const int Offline = 0x00001000;

        var raw = (int)attributes;
        return (raw & RecallOnDataAccess) != 0 ||
               (raw & RecallOnOpen) != 0 ||
               (raw & Offline) != 0;
    }

    private static FileRecord CreateFileRecord(FileInfo fi, Guid scanRunId, bool isCloudPlaceholder = false)
    {
        DateTime? creationUtc = null;
        DateTime? writeUtc = null;
        DateTime? accessUtc = null;
        var status = isCloudPlaceholder ? ScanMetadataStatus.CloudPlaceholder : ScanMetadataStatus.Ok;

        try
        {
            creationUtc = fi.CreationTimeUtc;
            writeUtc = fi.LastWriteTimeUtc;
            accessUtc = fi.LastAccessTimeUtc;
        }
        catch
        {
            if (!isCloudPlaceholder)
            {
                status = ScanMetadataStatus.Inaccessible;
            }
        }

        long sizeBytes = 0;
        try
        {
            sizeBytes = fi.Length;
        }
        catch
        {
            // Cloud placeholders that fail direct length check default to 0
        }

        return new FileRecord
        {
            ScanRunId = scanRunId,
            FullPath = fi.FullName,
            ParentPath = fi.DirectoryName ?? string.Empty,
            FileName = fi.Name,
            Extension = fi.Extension.ToLowerInvariant(),
            SizeBytes = sizeBytes,
            CreationTimeUtc = creationUtc,
            LastWriteTimeUtc = writeUtc,
            LastAccessTimeUtc = accessUtc,
            Attributes = (int)fi.Attributes,
            ObservedAtUtc = DateTime.UtcNow,
            MetadataStatus = status
        };
    }

    private async Task RecordErrorAsync(Guid scanRunId, string path, string op, Exception ex, CancellationToken cancellationToken)
    {
        var record = new ScanErrorRecord
        {
            ScanRunId = scanRunId,
            Path = path,
            Operation = op,
            ErrorCode = ex.GetType().Name,
            ErrorMessage = ex.Message,
            TimestampUtc = DateTime.UtcNow
        };
        try
        {
            await _repository.InsertScanErrorAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Database error recording failure must never crash the scanner
        }
    }

    public static List<string> ResolveTargetRoots(ScanTarget target)
    {
        var rawRoots = new List<string>();
        if (target.TargetPaths.Count > 0)
        {
            rawRoots.AddRange(target.TargetPaths);
        }
        else if (target.IncludeFixedDrivesByDefault)
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    rawRoots.Add(drive.RootDirectory.FullName);
                }
            }
        }

        return PruneNestedRoots(rawRoots);
    }

    public static List<string> PruneNestedRoots(IEnumerable<string> rawPaths)
    {
        var normalized = new List<string>();
        foreach (var p in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var full = Path.GetFullPath(p.Trim());
                if (Directory.Exists(full))
                {
                    var canonical = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    normalized.Add(canonical);
                }
            }
            catch
            {
                // Invalid path format is skipped safely
            }
        }

        normalized = normalized.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        var pruned = new List<string>();

        foreach (var candidate in normalized)
        {
            bool isNested = false;
            foreach (var existing in pruned)
            {
                if (candidate.StartsWith(existing, StringComparison.OrdinalIgnoreCase))
                {
                    isNested = true;
                    break;
                }
            }
            if (!isNested)
            {
                pruned.Add(candidate);
            }
        }

        return pruned.Select(p => p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p).ToList();
    }
}
