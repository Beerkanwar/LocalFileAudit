using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFileAudit.Core.Analysis;
using LocalFileAudit.Core.Interfaces;
using LocalFileAudit.Core.Models;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed class SizeFilterOption
{
    public string DisplayName { get; init; } = string.Empty;
    public long? MinBytes { get; init; }
}

public sealed class AgeFilterOption
{
    public string DisplayName { get; init; } = string.Empty;
    public int? DaysAgoMin { get; init; }
    public int? DaysAgoMax { get; init; }
    public DateTime? MinDateUtc => DaysAgoMin.HasValue ? DateTime.UtcNow.AddDays(-DaysAgoMin.Value) : null;
    public DateTime? MaxDateUtc => DaysAgoMax.HasValue ? DateTime.UtcNow.AddDays(-DaysAgoMax.Value) : null;

    public AgeFilterOption() { }
    public AgeFilterOption(string displayName, int? daysAgoMin = null, int? daysAgoMax = null)
    {
        DisplayName = displayName;
        DaysAgoMin = daysAgoMin;
        DaysAgoMax = daysAgoMax;
    }
}

public sealed class ScanRunOption
{
    public Guid? Id { get; init; }
    public string DisplayText { get; init; } = string.Empty;
}

public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly IScanRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly IReportGenerator _reportGenerator;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<InventoryViewModel> _logger;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _searchCts;

    public Action<string, Guid?>? OnNavigateToAiChat { get; set; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ScanRunOption? _selectedScanRun;

    [ObservableProperty]
    private string? _selectedExtension = "All Extensions";

    [ObservableProperty]
    private SizeFilterOption? _selectedSizeOption;

    [ObservableProperty]
    private AgeFilterOption? _selectedAgeOption;

    [ObservableProperty]
    private string _sortBy = "SizeBytes";

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 100;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private FileRecord? _selectedFile;

    [ObservableProperty]
    private string _selectedCategory = string.Empty;

    [ObservableProperty]
    private string _selectedExplanation = string.Empty;

    [ObservableProperty]
    private string _selectedVerificationHint = string.Empty;

    [ObservableProperty]
    private string _selectedAttributesText = string.Empty;

    [ObservableProperty]
    private string _selectedAllocatedSizeText = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _exportProgressPercent;

    [ObservableProperty]
    private string _exportStatusText = string.Empty;

    public ObservableCollection<FileRecord> Files { get; } = new();
    public ObservableCollection<ScanRunOption> AvailableScanRuns { get; } = new();
    public ObservableCollection<string> AvailableExtensions { get; } = new();
    public ObservableCollection<SizeFilterOption> SizeFilterOptions { get; } = new();
    public ObservableCollection<AgeFilterOption> AgeFilterOptions { get; } = new();

    public InventoryViewModel(
        IScanRepository repository,
        IConfigurationService configurationService,
        IReportGenerator reportGenerator,
        ILogger<InventoryViewModel> logger,
        INotificationService? notificationService = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;

        Title = "File Inventory";

        InitFilterOptions();
    }

    private void InitFilterOptions()
    {
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "All Sizes", MinBytes = null });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 10 MB", MinBytes = 10L * 1024 * 1024 });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 50 MB", MinBytes = 50L * 1024 * 1024 });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 100 MB", MinBytes = 100L * 1024 * 1024 });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 500 MB", MinBytes = 500L * 1024 * 1024 });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 1 GB", MinBytes = 1024L * 1024 * 1024 });
        SizeFilterOptions.Add(new SizeFilterOption { DisplayName = "> 5 GB", MinBytes = 5L * 1024 * 1024 * 1024 });
        SelectedSizeOption = SizeFilterOptions[0];

        AgeFilterOptions.Add(new AgeFilterOption("Any Age"));
        AgeFilterOptions.Add(new AgeFilterOption("Last 30 Days", daysAgoMin: 30));
        AgeFilterOptions.Add(new AgeFilterOption("Last 1 Year", daysAgoMin: 365));
        AgeFilterOptions.Add(new AgeFilterOption("Older than 2 Years", daysAgoMax: 730));
        AgeFilterOptions.Add(new AgeFilterOption("Older than 5 Years", daysAgoMax: 1825));
        SelectedAgeOption = AgeFilterOptions[0];
    }

    private bool _isInitialized;

    public async Task EnsureInitializedAsync(Guid? preferredScanId = null)
    {
        if (_isInitialized && preferredScanId == null) return;
        await LoadInitialDataAsync(preferredScanId);
    }

    public async Task LoadInitialDataAsync(Guid? preferredScanId = null)
    {
        _isInitialized = true;
        try
        {
            var scans = await _repository.GetRecentScanRunsAsync(20);
            AvailableScanRuns.Clear();
            AvailableScanRuns.Add(new ScanRunOption { Id = null, DisplayText = "(All Scans)" });

            ScanRunOption? targetScanOption = null;
            foreach (var s in scans)
            {
                var opt = new ScanRunOption
                {
                    Id = s.Id,
                    DisplayText = $"{s.StartTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {s.SelectedDrives} ({s.FilesDiscovered:N0} files)"
                };
                AvailableScanRuns.Add(opt);

                if (preferredScanId.HasValue && s.Id == preferredScanId.Value)
                {
                    targetScanOption = opt;
                }
            }

            SelectedScanRun = targetScanOption ?? (AvailableScanRuns.Count > 1 ? AvailableScanRuns[1] : AvailableScanRuns[0]);

            await RefreshExtensionsAsync();
            await ExecuteSearchAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load initial inventory data.");
        }
    }

    public async Task RefreshExtensionsAsync()
    {
        try
        {
            var exts = await _repository.GetDiscoveredExtensionsAsync(SelectedScanRun?.Id);
            AvailableExtensions.Clear();
            AvailableExtensions.Add("All Extensions");
            foreach (var e in exts.Take(100))
            {
                AvailableExtensions.Add(e);
            }
            SelectedExtension = "All Extensions";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh discovered extensions.");
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        CurrentPage = 1;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    public async Task ResetFiltersAsync()
    {
        SearchText = string.Empty;
        SelectedExtension = "All Extensions";
        SelectedSizeOption = SizeFilterOptions[0];
        SelectedAgeOption = AgeFilterOptions[0];
        SortBy = "SizeBytes";
        SortDescending = true;
        CurrentPage = 1;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            await ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    public async Task FirstPageAsync()
    {
        if (CurrentPage > 1)
        {
            CurrentPage = 1;
            await ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    public async Task LastPageAsync()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage = TotalPages;
            await ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    public async Task ChangeSortAsync(string column)
    {
        if (SortBy.Equals(column, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortBy = column;
            SortDescending = true;
        }
        CurrentPage = 1;
        await ExecuteSearchAsync();
    }

    public async Task ExecuteSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsLoading = true;
        StatusMessage = "Querying inventory database...";

        try
        {
            var ext = (SelectedExtension == "All Extensions" || string.IsNullOrWhiteSpace(SelectedExtension))
                ? null
                : SelectedExtension;

            var query = new FileInventoryQuery
            {
                ScanRunId = SelectedScanRun?.Id,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Extension = ext,
                MinSizeBytes = SelectedSizeOption?.MinBytes,
                MinModifiedUtc = SelectedAgeOption?.MinDateUtc,
                MaxModifiedUtc = SelectedAgeOption?.MaxDateUtc,
                SortBy = SortBy,
                SortDescending = SortDescending,
                Offset = (CurrentPage - 1) * PageSize,
                Limit = PageSize
            };

            var paged = await _repository.QueryFilesAsync(query, token);
            token.ThrowIfCancellationRequested();

            Files.Clear();
            foreach (var item in paged.Items)
            {
                Files.Add(item);
            }

            TotalCount = paged.TotalCount;
            TotalPages = Math.Max(1, paged.TotalPages);
            StatusMessage = $"Showing {Files.Count:N0} of {TotalCount:N0} files (Page {CurrentPage} of {TotalPages})";

            if (Files.Count > 0 && SelectedFile == null)
            {
                SelectedFile = Files[0];
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled in favor of newer query
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search error: {ex.Message}";
            _logger.LogError(ex, "Failed to execute inventory search");
        }
        finally
        {
            if (_searchCts?.Token == token)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnSelectedFileChanged(FileRecord? value)
    {
        if (value == null)
        {
            SelectedCategory = string.Empty;
            SelectedExplanation = string.Empty;
            SelectedVerificationHint = string.Empty;
            SelectedAttributesText = string.Empty;
            return;
        }

        try
        {
            var config = _configurationService.Current;
            var eval = CandidateRuleEvaluator.Classify(value, config);

            SelectedCategory = eval?.Category.ToString() ?? "Keep on laptop";
            SelectedExplanation = eval?.Explanation ?? "File is within standard operating parameters and does not trigger review rules.";
            SelectedVerificationHint = eval?.Category switch
            {
                CandidateCategory.ArchiveCandidate => "Verify file is non-active before archiving to external storage.",
                CandidateCategory.CleanupCandidate => "Confirm file is an installer or expendable cache before manual review.",
                CandidateCategory.Investigate => "Inspect parent directory context and last write timestamp.",
                CandidateCategory.ProtectedSystem => "Protected operating system / program location. Strictly keep untouched.",
                _ => "No cleanup action recommended."
            };

            var attrList = new List<string>();
            var attr = (FileAttributes)value.Attributes;
            if (attr.HasFlag(FileAttributes.ReadOnly)) attrList.Add("Read-Only");
            if (attr.HasFlag(FileAttributes.Hidden)) attrList.Add("Hidden");
            if (attr.HasFlag(FileAttributes.System)) attrList.Add("System");
            if (attr.HasFlag(FileAttributes.Archive)) attrList.Add("Archive");
            if (attr.HasFlag(FileAttributes.Compressed)) attrList.Add("Compressed");
            if (attr.HasFlag(FileAttributes.ReparsePoint)) attrList.Add("Reparse Point");
            if (value.MetadataStatus == ScanMetadataStatus.CloudPlaceholder) attrList.Add("Cloud Placeholder");
            SelectedAttributesText = attrList.Count > 0 ? string.Join(", ", attrList) : "Normal";

            // On-demand allocated (on-disk) size via Win32 GetCompressedFileSize (non-blocking)
            if (value.MetadataStatus == ScanMetadataStatus.CloudPlaceholder)
            {
                SelectedAllocatedSizeText = "0 bytes (Cloud Placeholder / On-demand)";
            }
            else
            {
                SelectedAllocatedSizeText = "Querying allocated size...";
                var targetPath = value.FullPath;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var allocated = LocalFileAudit.Infrastructure.Scanning.FileAllocationHelper.GetAllocatedSizeBytes(targetPath);
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            if (SelectedFile?.FullPath == targetPath)
                            {
                                SelectedAllocatedSizeText = allocated.HasValue
                                    ? $"{allocated.Value / (1024.0 * 1024.0):F2} MB ({allocated.Value:N0} bytes)"
                                    : "N/A (Offline or Inaccessible)";
                            }
                        });
                    }
                    catch
                    {
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            if (SelectedFile?.FullPath == targetPath)
                            {
                                SelectedAllocatedSizeText = "N/A (Offline or Inaccessible)";
                            }
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate candidate metadata for file {Path}", value.FullPath);
        }
    }

    [RelayCommand]
    public void CopyPath()
    {
        if (SelectedFile != null && !string.IsNullOrWhiteSpace(SelectedFile.FullPath))
        {
            try
            {
                Clipboard.SetText(SelectedFile.FullPath);
                StatusMessage = $"Copied path to clipboard: {SelectedFile.FileName}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy file path to clipboard.");
                StatusMessage = "Unable to copy path (clipboard may be locked by another application).";
            }
        }
    }

    [RelayCommand]
    public void RevealInExplorer()
    {
        if (SelectedFile == null || string.IsNullOrWhiteSpace(SelectedFile.FullPath)) return;

        try
        {
            // Opens Explorer with the file highlighted without executing or mutating it
            var args = $"/select,\"{SelectedFile.FullPath}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reveal file in Windows Explorer: {Path}", SelectedFile.FullPath);
            StatusMessage = $"Unable to open explorer: {ex.Message}";
        }
    }

    public async Task SelectAndInspectFileByIdAsync(long fileId, Guid? preferredScanId = null)
    {
        try
        {
            var file = await _repository.GetFileByIdAsync(fileId);
            if (file == null)
            {
                StatusMessage = $"File record with ID {fileId} was not found in database.";
                return;
            }

            var scanId = file.ScanRunId;
            if (SelectedScanRun == null || SelectedScanRun.Id != scanId)
            {
                await LoadInitialDataAsync(scanId);
            }

            SearchText = file.FileName;
            CurrentPage = 1;
            await ExecuteSearchAsync();

            var matched = Files.FirstOrDefault(f => f.Id == fileId) ?? file;
            SelectedFile = matched;
            StatusMessage = $"Selected and inspected candidate: {file.FileName} (ID: {fileId})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect candidate file ID {FileId}", fileId);
            StatusMessage = $"Navigation to candidate failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ExportFullInventoryAsync()
    {
        if (IsExporting) return;

        if (SelectedScanRun == null || !SelectedScanRun.Id.HasValue)
        {
            MessageBox.Show("Please select a completed scan run before exporting inventory.", "Export Inventory", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var scanId = SelectedScanRun.Id.Value;
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Full File Inventory to CSV",
            FileName = $"Inventory-Full-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog() != true)
        {
            return;
        }

        var targetPath = sfd.FileName;
        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgressPercent = 0;
        ExportStatusText = "Preparing streaming inventory export...";

        try
        {
            var filter = new FileInventoryExportFilter
            {
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Extension = (SelectedExtension == "All Extensions" || string.IsNullOrWhiteSpace(SelectedExtension)) ? null : SelectedExtension,
                MinSizeBytes = SelectedSizeOption?.MinBytes,
                MinModifiedUtc = SelectedAgeOption?.MinDateUtc,
                MaxModifiedUtc = SelectedAgeOption?.MaxDateUtc,
                OnlyCandidates = false
            };

            var progress = new Progress<double>(p =>
            {
                ExportProgressPercent = p;
                ExportStatusText = $"Exporting inventory: {p:F0}%";
            });

            var count = await _reportGenerator.ExportFullInventoryCsvAsync(scanId, targetPath, filter, progress, _exportCts.Token);
            ExportStatusText = $"Export complete! {count:N0} files written.";
            StatusMessage = $"Full inventory exported: {count:N0} records to {Path.GetFileName(targetPath)}";

            _notificationService?.NotifyScanCompleted(
                "Inventory Export Complete",
                $"Successfully exported {count:N0} file records to {Path.GetFileName(targetPath)}",
                isSuccess: true);
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "Export cancelled.";
            StatusMessage = "Full inventory export was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export full inventory to {Path}", targetPath);
            ExportStatusText = $"Export error: {ex.Message}";
            MessageBox.Show($"Failed to export inventory:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand]
    public void CancelExport()
    {
        if (IsExporting && _exportCts != null && !_exportCts.IsCancellationRequested)
        {
            _exportCts.Cancel();
            ExportStatusText = "Cancelling export...";
        }
    }

    [RelayCommand]
    public void AskAiAboutSelectedFile()
    {
        if (SelectedFile == null) return;
        var query = $"Can you explain file '{SelectedFile.FileName}' ({SelectedFile.SizeBytes:N0} bytes, extension: {SelectedFile.Extension}) in scan {SelectedScanRun?.Id}? What is the recommended retention or audit policy?";
        OnNavigateToAiChat?.Invoke(query, SelectedScanRun?.Id);
    }
}
