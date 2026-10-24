using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class CompareRunsViewModel : ViewModelBase
{
    private readonly IScanRepository _repository;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<CompareRunsViewModel> _logger;

    [ObservableProperty]
    private ScanRunOption? _selectedBaselineRun;

    [ObservableProperty]
    private ScanRunOption? _selectedTargetRun;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasComparisonResult;

    [ObservableProperty]
    private string _statusMessage = "Select baseline (older) and target (newer) scan runs to compare filesystem deltas.";

    [ObservableProperty]
    private string _newFilesText = "0 files (0 B)";

    [ObservableProperty]
    private string _deletedFilesText = "0 files (0 B)";

    [ObservableProperty]
    private string _modifiedFilesText = "0 files";

    [ObservableProperty]
    private string _netStorageDeltaText = "0 B";

    [ObservableProperty]
    private string _selectedDeltaFilter = "All";

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private RunComparisonResult? _comparisonResult;

    [ObservableProperty]
    private string _resultsCountText = string.Empty;

    [ObservableProperty]
    private bool _isTruncated;

    public ObservableCollection<ScanRunOption> AvailableRuns { get; } = new();
    public ObservableCollection<FileDeltaRecord> FilteredDeltas { get; } = new();
    public ObservableCollection<ExtensionDelta> ExtensionDeltas { get; } = new();
    public ObservableCollection<string> DeltaFilterOptions { get; } = new() { "All", "New", "Deleted", "Modified" };

    public CompareRunsViewModel(
        IScanRepository repository,
        ILogger<CompareRunsViewModel> logger,
        INotificationService? notificationService = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;

        Title = "Run Comparison";
    }

    public async Task LoadAvailableRunsAsync()
    {
        try
        {
            var runs = await _repository.GetRecentScanRunsAsync(50);
            var completed = runs.Where(r => r.Status == ScanState.Completed).ToList();

            AvailableRuns.Clear();
            foreach (var r in completed)
            {
                var text = $"{r.StartTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {r.FilesDiscovered:N0} files ({r.SelectedDrives})";
                AvailableRuns.Add(new ScanRunOption { Id = r.Id, DisplayText = text });
            }

            if (AvailableRuns.Count >= 2)
            {
                // Default baseline to second latest, target to latest
                SelectedTargetRun = AvailableRuns[0];
                SelectedBaselineRun = AvailableRuns[1];
            }
            else if (AvailableRuns.Count == 1)
            {
                SelectedTargetRun = AvailableRuns[0];
                SelectedBaselineRun = AvailableRuns[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load runs for comparison");
            StatusMessage = $"Failed to load scans: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task CompareRunsAsync()
    {
        if (IsLoading) return;

        if (SelectedBaselineRun == null || !SelectedBaselineRun.Id.HasValue ||
            SelectedTargetRun == null || !SelectedTargetRun.Id.HasValue)
        {
            MessageBox.Show("Please select both a baseline and a target scan run.", "Run Comparison", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedBaselineRun.Id.Value == SelectedTargetRun.Id.Value)
        {
            MessageBox.Show("Baseline and Target runs are identical. Please choose two distinct scan runs to evaluate changes.", "Run Comparison", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsLoading = true;
        StatusMessage = "Analyzing filesystem deltas between scan runs in SQLite...";

        try
        {
            var baselineId = SelectedBaselineRun.Id.Value;
            var targetId = SelectedTargetRun.Id.Value;

            var result = await _repository.CompareScanRunsAsync(baselineId, targetId);
            ComparisonResult = result;

            NewFilesText = $"{result.NewFilesCount:N0} files ({FormatBytes(result.NewFilesBytes)})";
            DeletedFilesText = $"{result.DeletedFilesCount:N0} files ({FormatBytes(result.DeletedFilesBytes)})";
            ModifiedFilesText = $"{result.ModifiedFilesCount:N0} files";

            var sign = result.NetStorageDeltaBytes > 0 ? "+" : "";
            NetStorageDeltaText = $"{sign}{FormatBytes(result.NetStorageDeltaBytes)}";

            ExtensionDeltas.Clear();
            foreach (var ext in result.ExtensionDeltas.Take(25))
            {
                ExtensionDeltas.Add(ext);
            }

            ApplyFilter();
            HasComparisonResult = true;
            if (!string.IsNullOrEmpty(result.WarningMessage))
            {
                StatusMessage = $"Warning: {result.WarningMessage} ({result.Deltas.Count:N0} file changes)";
                MessageBox.Show(result.WarningMessage, "Scan Roots Mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StatusMessage = $"Comparison complete: {result.Deltas.Count:N0} total file changes discovered ({sign}{FormatBytes(result.NetStorageDeltaBytes)} net storage)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing run comparison");
            StatusMessage = $"Comparison failed: {ex.Message}";
            MessageBox.Show($"Failed to compare scan runs:\n\n{ex.Message}", "Comparison Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private CancellationTokenSource? _searchDebounceCts;

    partial void OnSelectedDeltaFilterChanged(string value) => ApplyFilter();
    partial void OnSearchFilterChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    Application.Current?.Dispatcher?.Invoke(ApplyFilter);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    [RelayCommand]
    public void ApplyFilter()
    {
        if (ComparisonResult == null)
        {
            FilteredDeltas.Clear();
            return;
        }

        var query = ComparisonResult.Deltas.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SelectedDeltaFilter) && SelectedDeltaFilter != "All")
        {
            if (Enum.TryParse<DeltaType>(SelectedDeltaFilter, ignoreCase: true, out var type))
            {
                query = query.Where(d => d.DeltaType == type);
            }
        }

        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            var term = SearchFilter.Trim();
            query = query.Where(d => d.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var allDeltas = query.ToList();
        var total = allDeltas.Count;

        FilteredDeltas.Clear();
        foreach (var item in allDeltas.Take(1000))
        {
            FilteredDeltas.Add(item);
        }

        IsTruncated = total > 1000;
        ResultsCountText = total > 1000
            ? $"Showing 1,000 of {total:N0} records (Export CSV to view full dataset)"
            : $"{total:N0} records";
    }

    [RelayCommand]
    public async Task ExportDiffCsvAsync()
    {
        if (ComparisonResult == null || ComparisonResult.Deltas.Count == 0)
        {
            MessageBox.Show("No comparison data available to export.", "Export Comparison", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Run Comparison Changes to CSV",
            FileName = $"CompareRuns-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog() != true) return;

        try
        {
            await _repository.ExportComparisonCsvAsync(ComparisonResult, sfd.FileName);
            StatusMessage = $"Exported {ComparisonResult.Deltas.Count:N0} changes to {Path.GetFileName(sfd.FileName)}";
            _notificationService?.NotifyScanCompleted(
                "Comparison Export Complete",
                $"Successfully exported {ComparisonResult.Deltas.Count:N0} delta rows to {Path.GetFileName(sfd.FileName)}",
                isSuccess: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export comparison CSV to {Path}", sfd.FileName);
            MessageBox.Show($"Failed to export comparison CSV:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        var abs = Math.Abs(bytes);
        if (abs >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (abs >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        if (abs >= 1024L)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes:N0} B";
    }
}
