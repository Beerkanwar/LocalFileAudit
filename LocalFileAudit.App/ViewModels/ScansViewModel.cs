using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed class DriveSelectionItem : ObservableObject
{
    private bool _isSelected = true;

    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed partial class ScansViewModel : ViewModelBase
{
    private readonly IScanEngine _scanEngine;
    private readonly IScanRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<ScansViewModel> _logger;

    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _currentDirectory = "Ready";

    [ObservableProperty]
    private long _filesDiscovered;

    [ObservableProperty]
    private long _directoriesCompleted;

    [ObservableProperty]
    private double _filesPerSecond;

    [ObservableProperty]
    private TimeSpan _elapsedTime;

    [ObservableProperty]
    private long _errorCount;

    [ObservableProperty]
    private string _scanStatusText = "Idle — Ready to scan";

    [ObservableProperty]
    private ScanRun? _incompleteScan;

    public bool HasIncompleteScan => IncompleteScan != null;

    partial void OnIncompleteScanChanged(ScanRun? value)
        => OnPropertyChanged(nameof(HasIncompleteScan));

    [ObservableProperty]
    private string _customDirectoryPath = string.Empty;

    public ObservableCollection<DriveSelectionItem> AvailableDrives { get; } = new();
    public ObservableCollection<ScanRun> RecentScans { get; } = new();

    public Func<Guid, Task>? OnNavigateToInventoryWithScan { get; set; }
    public Func<Guid, Task>? OnNavigateToAnalysisWithScan { get; set; }

    [RelayCommand]
    public async Task ViewInventoryForScanAsync(Guid scanRunId)
    {
        if (OnNavigateToInventoryWithScan != null)
        {
            try
            {
                await OnNavigateToInventoryWithScan(scanRunId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to inventory with scan {ScanId}", scanRunId);
            }
        }
    }

    [RelayCommand]
    public async Task ViewAnalysisForScanAsync(Guid scanRunId)
    {
        if (OnNavigateToAnalysisWithScan != null)
        {
            try
            {
                await OnNavigateToAnalysisWithScan(scanRunId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to analysis with scan {ScanId}", scanRunId);
            }
        }
    }

    [RelayCommand]
    public async Task DeleteScanHistoryAsync(Guid scanRunId)
    {
        try
        {
            await _repository.DiscardScanRunAsync(scanRunId);
            ScanStatusText = $"Scan history record {scanRunId} removed from database. Source files untouched.";
            await RefreshScansAsync();
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Failed to remove scan history: {ex.Message}";
            _logger.LogError(ex, "Failed to discard scan run {ScanRunId}", scanRunId);
        }
    }

    public ScansViewModel(
        IScanEngine scanEngine,
        IScanRepository repository,
        IConfigurationService configurationService,
        INotificationService notificationService,
        ILogger<ScansViewModel> logger)
    {
        _scanEngine = scanEngine ?? throw new ArgumentNullException(nameof(scanEngine));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Scans";

        LoadDrives();
        _ = RefreshScansAsync();
    }

    private void LoadDrives()
    {
        AvailableDrives.Clear();
        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var d in drives)
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                {
                    var totalGb = d.TotalSize / (1024 * 1024 * 1024);
                    var freeGb = d.TotalFreeSpace / (1024 * 1024 * 1024);
                    AvailableDrives.Add(new DriveSelectionItem
                    {
                        Name = d.Name,
                        RootPath = d.RootDirectory.FullName,
                        Description = $"{d.Name} ({d.DriveFormat}, {totalGb} GB Total, {freeGb} GB Free)",
                        IsSelected = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate local drives.");
        }
    }

    public async Task RefreshScansAsync()
    {
        try
        {
            var recent = await _repository.GetRecentScanRunsAsync(20);
            RecentScans.Clear();
            foreach (var r in recent)
            {
                RecentScans.Add(r);
            }

            var incomplete = await _repository.GetIncompleteScanRunsAsync();
            IncompleteScan = incomplete.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recent or incomplete scans.");
        }
    }

    [RelayCommand]
    public async Task StartScanAsync()
    {
        if (IsScanning) return;

        var selectedRoots = AvailableDrives.Where(d => d.IsSelected).Select(d => d.RootPath).ToList();

        if (!string.IsNullOrWhiteSpace(CustomDirectoryPath))
        {
            selectedRoots.Add(CustomDirectoryPath.Trim());
        }

        if (selectedRoots.Count == 0)
        {
            ScanStatusText = "Please select at least one drive or specify a directory path.";
            return;
        }

        var config = _configurationService.Current;
        var target = new ScanTarget
        {
            TargetPaths = selectedRoots,
            FollowReparsePoints = config.Scanning.FollowReparsePoints,
            LowImpactMode = config.Scanning.LowImpactMode
        };

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        IsPaused = false;
        ScanStatusText = "Scanning...";

        var progress = new Progress<ScanProgress>(OnProgressReported);

        try
        {
            var run = await _scanEngine.StartScanAsync(target, _scanCts.Token, progress);
            if (run.Status == ScanState.Completed)
            {
                var errorSuffix = run.ErrorCount > 0
                    ? $" ({run.ErrorCount:N0} inaccessible paths encountered — detailed in ScanErrors.csv)"
                    : string.Empty;

                ScanStatusText = $"Scan completed: {run.FilesDiscovered:N0} files inventoried in {ElapsedTime:hh\\:mm\\:ss}{errorSuffix}.";
                _notificationService.NotifyScanCompleted(
                    "Scan Complete",
                    run.ErrorCount > 0
                        ? $"{run.FilesDiscovered:N0} files inventoried safely ({run.ErrorCount:N0} skipped paths in ScanErrors.csv)."
                        : $"{run.FilesDiscovered:N0} files inventoried safely in {ElapsedTime:mm\\:ss}.",
                    isSuccess: true);
            }
            else
            {
                ScanStatusText = $"Scan {run.Status}: {run.FilesDiscovered:N0} files recorded.";
                _notificationService.NotifyScanCompleted(
                    $"Scan {run.Status}",
                    $"{run.FilesDiscovered:N0} files recorded before interruption.",
                    isSuccess: false);
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Scan cancelled by user. Committed batches preserved in database.";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Scan failed: {ex.Message}";
            _logger.LogError(ex, "Scan execution error");
        }
        finally
        {
            IsScanning = false;
            IsPaused = false;
            CurrentDirectory = "Ready";
            _scanCts?.Dispose();
            _scanCts = null;
            await RefreshScansAsync();
        }
    }

    [RelayCommand]
    public void PauseScan()
    {
        if (IsScanning && !IsPaused)
        {
            _scanEngine.PauseScan();
            IsPaused = true;
            ScanStatusText = "Scan paused.";
        }
    }

    [RelayCommand]
    public void ResumeScan()
    {
        if (IsScanning && IsPaused)
        {
            _scanEngine.ResumeScanExecution();
            IsPaused = false;
            ScanStatusText = "Resuming scan...";
        }
    }

    [RelayCommand]
    public void CancelScan()
    {
        if (IsScanning && _scanCts != null)
        {
            ScanStatusText = "Cancelling scan... (flushing pending batches)";
            _scanCts.Cancel();
        }
    }

    [RelayCommand]
    public async Task ResumeIncompleteScanAsync()
    {
        if (IncompleteScan == null || IsScanning) return;

        var runId = IncompleteScan.Id;
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        IsPaused = false;
        ScanStatusText = $"Resuming incomplete scan {runId}...";

        var progress = new Progress<ScanProgress>(OnProgressReported);

        try
        {
            var run = await _scanEngine.ResumeScanAsync(runId, _scanCts.Token, progress);
            var resumeErrorSuffix = run.ErrorCount > 0
                ? $" ({run.ErrorCount:N0} inaccessible paths encountered — detailed in ScanErrors.csv)"
                : string.Empty;
            ScanStatusText = $"Scan resumed and {run.Status}: {run.FilesDiscovered:N0} total files{resumeErrorSuffix}.";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Failed to resume scan: {ex.Message}";
            _logger.LogError(ex, "Failed to resume incomplete scan");
        }
        finally
        {
            IsScanning = false;
            IsPaused = false;
            CurrentDirectory = "Ready";
            _scanCts?.Dispose();
            _scanCts = null;
            await RefreshScansAsync();
        }
    }

    [RelayCommand]
    public async Task DiscardIncompleteScanAsync()
    {
        if (IncompleteScan == null || IsScanning) return;

        try
        {
            var runId = IncompleteScan.Id;
            await _repository.DiscardScanRunAsync(runId);
            IncompleteScan = null;
            ScanStatusText = "Incomplete scan records discarded from database. Source files untouched.";
            await RefreshScansAsync();
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Failed to discard scan: {ex.Message}";
            _logger.LogError(ex, "Failed to discard scan");
        }
    }

    private void OnProgressReported(ScanProgress p)
    {
        CurrentDirectory = p.CurrentDirectory;
        FilesDiscovered = p.FilesDiscovered;
        DirectoriesCompleted = p.DirectoriesCompleted;
        FilesPerSecond = p.FilesPerSecond;
        ElapsedTime = p.ElapsedTime;
        ErrorCount = p.ErrorCount;

        if (p.StatusDetail != null)
        {
            ScanStatusText = p.StatusDetail;
        }
    }
}
