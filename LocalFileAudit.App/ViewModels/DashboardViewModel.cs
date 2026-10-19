using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IScanRepository _repository;
    private readonly ILogger<DashboardViewModel> _logger;

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

    public DashboardViewModel(IScanRepository repository, ILogger<DashboardViewModel> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Dashboard";
        _ = LoadLatestMetricsAsync();
    }

    public async Task LoadLatestMetricsAsync()
    {
        try
        {
            var recent = await _repository.GetRecentScanRunsAsync(1);
            var latest = recent.FirstOrDefault();
            if (latest != null)
            {
                var filesCount = await _repository.GetFileCountForScanAsync(latest.Id);
                var totalBytes = await _repository.GetTotalLogicalSizeBytesForScanAsync(latest.Id);

                TotalFilesDiscoveredText = $"{filesCount:N0} files";
                TotalLogicalSizeText = $"{totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
                DrivesCountText = $"{latest.SelectedDrives.Split(';', StringSplitOptions.RemoveEmptyEntries).Length} roots";
                ScanStateText = $"Latest: {latest.Status}";
                LastScanTimeText = latest.StartTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
                DrivesCountText = $"{drives.Count} drives detected";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load dashboard metrics from SQLite.");
        }
    }
}
