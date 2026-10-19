using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IScanRepository _repository;
    private readonly IStorageAnalysisEngine _analysisEngine;
    private readonly ILogger<DashboardViewModel> _logger;

    public Action<string>? OnNavigateRequested { get; set; }

    [ObservableProperty]
    private string _totalFilesDiscoveredText = "0 files";

    [ObservableProperty]
    private string _totalLogicalSizeText = "0.00 GB";

    [ObservableProperty]
    private string _drivesCountText = "0 drives";

    [ObservableProperty]
    private string _scanStateText = "Idle — Ready to scan";

    [ObservableProperty]
    private string _lastScanTimeText = "Never";

    [ObservableProperty]
    private string _errorCountText = "0 errors";

    [ObservableProperty]
    private string _archiveCandidatesCountText = "0 items";

    [ObservableProperty]
    private string _cleanupCandidatesCountText = "0 items";

    [ObservableProperty]
    private bool _hasScanData;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<ExtensionSummary> TopExtensions { get; } = new();
    public ObservableCollection<DirectorySummary> LargestDirectories { get; } = new();
    public ObservableCollection<FileRecord> LargestFiles { get; } = new();

    public DashboardViewModel(
        IScanRepository repository,
        IStorageAnalysisEngine analysisEngine,
        ILogger<DashboardViewModel> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Dashboard";
        _ = LoadLatestMetricsAsync();
    }

    public async Task LoadLatestMetricsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var recent = await _repository.GetRecentScanRunsAsync(1);
            var latest = recent.FirstOrDefault();
            if (latest != null)
            {
                HasScanData = true;
                var filesCount = await _repository.GetFileCountForScanAsync(latest.Id);
                var totalBytes = await _repository.GetTotalLogicalSizeBytesForScanAsync(latest.Id);

                TotalFilesDiscoveredText = $"{filesCount:N0} files";
                TotalLogicalSizeText = $"{totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
                DrivesCountText = $"{latest.SelectedDrives.Split(';', StringSplitOptions.RemoveEmptyEntries).Length} roots";
                ScanStateText = $"{latest.Status}";
                LastScanTimeText = latest.StartTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                ErrorCountText = $"{latest.ErrorCount:N0} errors";

                // Load deterministic metrics for dashboard charts & hotspots
                var metrics = await _analysisEngine.AnalyzeScanAsync(latest.Id);

                TopExtensions.Clear();
                foreach (var ext in metrics.TopExtensions.Take(5))
                {
                    TopExtensions.Add(ext);
                }

                LargestDirectories.Clear();
                foreach (var dir in metrics.LargestDirectories.Take(5))
                {
                    LargestDirectories.Add(dir);
                }

                LargestFiles.Clear();
                foreach (var file in metrics.LargestFiles.Take(5))
                {
                    LargestFiles.Add(file);
                }

                var archiveCount = metrics.ReviewCandidates.Count(c => c.Category == CandidateCategory.ArchiveCandidate);
                var cleanupCount = metrics.ReviewCandidates.Count(c => c.Category == CandidateCategory.CleanupCandidate);
                ArchiveCandidatesCountText = $"{archiveCount:N0} candidates";
                CleanupCandidatesCountText = $"{cleanupCount:N0} candidates";
            }
            else
            {
                HasScanData = false;
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
                DrivesCountText = $"{drives.Count} drives detected";
                TotalFilesDiscoveredText = "0 files";
                TotalLogicalSizeText = "0.00 GB";
                ScanStateText = "No scans yet";
                LastScanTimeText = "Never";
                ErrorCountText = "0 errors";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dashboard metrics from SQLite.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        OnNavigateRequested?.Invoke(viewName);
    }
}
