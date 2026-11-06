using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace LocalFileAudit.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    [ObservableProperty]
    private string _statusMessage = "Ready — Strict Read-Only Mode Active";

    [ObservableProperty]
    private string _appVersion = $"v{LocalFileAudit.Core.Common.AppVersion.Current}";

    [ObservableProperty]
    private bool _isSafeModeActive = true;

    [ObservableProperty]
    private string _activeView = "Dashboard";

    public bool IsDashboardActive => ActiveView == "Dashboard";
    public bool IsScansActive => ActiveView == "Scans";
    public bool IsInventoryActive => ActiveView == "Inventory";
    public bool IsStorageAnalysisActive => ActiveView == "StorageAnalysis";
    public bool IsDuplicateAnalysisActive => ActiveView == "DuplicateAnalysis";
    public bool IsAiReportsActive => ActiveView == "AiReports";
    public bool IsCompareRunsActive => ActiveView == "CompareRuns";
    public bool IsArchivePlanActive => ActiveView == "ArchivePlan";
    public bool IsArchiveVerificationActive => ActiveView == "ArchiveVerification";
    public bool IsSettingsActive => ActiveView == "Settings";

    public DashboardViewModel Dashboard { get; }
    public ScansViewModel Scans { get; }
    public InventoryViewModel Inventory { get; }
    public StorageAnalysisViewModel StorageAnalysis { get; }
    public DuplicateAnalysisViewModel DuplicateAnalysis { get; }
    public AiReportsViewModel AiReports { get; }
    public CompareRunsViewModel CompareRuns { get; }
    public ArchivePlanViewModel ArchivePlan { get; }
    public ArchiveVerificationViewModel ArchiveVerification { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        DashboardViewModel dashboard,
        ScansViewModel scans,
        InventoryViewModel inventory,
        StorageAnalysisViewModel storageAnalysis,
        DuplicateAnalysisViewModel duplicateAnalysis,
        AiReportsViewModel aiReports,
        CompareRunsViewModel compareRuns,
        ArchivePlanViewModel archivePlan,
        ArchiveVerificationViewModel archiveVerification,
        SettingsViewModel settings,
        ILogger<MainViewModel> logger)
    {
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        Scans = scans ?? throw new ArgumentNullException(nameof(scans));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        StorageAnalysis = storageAnalysis ?? throw new ArgumentNullException(nameof(storageAnalysis));
        DuplicateAnalysis = duplicateAnalysis ?? throw new ArgumentNullException(nameof(duplicateAnalysis));
        AiReports = aiReports ?? throw new ArgumentNullException(nameof(aiReports));
        CompareRuns = compareRuns ?? throw new ArgumentNullException(nameof(compareRuns));
        ArchivePlan = archivePlan ?? throw new ArgumentNullException(nameof(archivePlan));
        ArchiveVerification = archiveVerification ?? throw new ArgumentNullException(nameof(archiveVerification));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentViewModel = Dashboard;

        // Wire inter-viewModel navigation callbacks
        Dashboard.OnNavigateRequested = view => Navigate(view);
        Scans.OnNavigateToInventoryWithScan = async scanId =>
        {
            try
            {
                await Inventory.LoadInitialDataAsync(scanId);
                Navigate("Inventory");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to inventory with scan {ScanId}", scanId);
            }
        };
        Scans.OnNavigateToAnalysisWithScan = async scanId =>
        {
            try
            {
                await StorageAnalysis.LoadAnalysisAsync(scanId);
                Navigate("StorageAnalysis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to analysis with scan {ScanId}", scanId);
            }
        };
        AiReports.OnNavigateToInventoryWithCandidate = async (candidateId, scanId) =>
        {
            try
            {
                Navigate("Inventory");
                await Inventory.SelectAndInspectFileByIdAsync(candidateId, scanId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed navigating to inventory with candidate {CandidateId}", candidateId);
            }
        };
        Inventory.OnNavigateToAiChat = (query, scanId) =>
        {
            Navigate("AiReports");
            if (scanId != null)
            {
                var targetScan = AiReports.AvailableScans.FirstOrDefault(s => s.Id == scanId.Value);
                if (targetScan != null) AiReports.SelectedScan = targetScan;
            }
            AiReports.ChatInputText = query;
        };
    }

    [RelayCommand]
    public void Navigate(string viewName)
    {
        _logger.LogInformation("Navigating to view: {ViewName}", viewName);

        ActiveView = viewName;
        OnPropertyChanged(nameof(IsDashboardActive));
        OnPropertyChanged(nameof(IsScansActive));
        OnPropertyChanged(nameof(IsInventoryActive));
        OnPropertyChanged(nameof(IsStorageAnalysisActive));
        OnPropertyChanged(nameof(IsDuplicateAnalysisActive));
        OnPropertyChanged(nameof(IsAiReportsActive));
        OnPropertyChanged(nameof(IsCompareRunsActive));
        OnPropertyChanged(nameof(IsArchivePlanActive));
        OnPropertyChanged(nameof(IsArchiveVerificationActive));
        OnPropertyChanged(nameof(IsSettingsActive));

        CurrentViewModel = viewName switch
        {
            "Dashboard" => Dashboard,
            "Scans" => Scans,
            "Inventory" => Inventory,
            "StorageAnalysis" => StorageAnalysis,
            "DuplicateAnalysis" => DuplicateAnalysis,
            "AiReports" => AiReports,
            "CompareRuns" => CompareRuns,
            "ArchivePlan" => ArchivePlan,
            "ArchiveVerification" => ArchiveVerification,
            "Settings" => Settings,
            _ => Dashboard
        };

        // Proactively refresh target view state
        switch (viewName)
        {
            case "Dashboard":
                SafeExecuteNavigationAsync(() => Dashboard.LoadLatestMetricsAsync(), "Dashboard");
                break;
            case "Scans":
                SafeExecuteNavigationAsync(() => Scans.RefreshScansAsync(), "Scans");
                break;
            case "Inventory":
                SafeExecuteNavigationAsync(() => Inventory.EnsureInitializedAsync(), "Inventory");
                break;
            case "StorageAnalysis":
                SafeExecuteNavigationAsync(async () =>
                {
                    await StorageAnalysis.EnsureInitializedAsync();
                    await StorageAnalysis.RefreshGeneratedReportsAsync();
                }, "StorageAnalysis");
                break;
            case "DuplicateAnalysis":
                SafeExecuteNavigationAsync(() => DuplicateAnalysis.EnsureInitializedAsync(), "DuplicateAnalysis");
                break;
            case "AiReports":
                SafeExecuteNavigationAsync(() => AiReports.EnsureInitializedAsync(), "AiReports");
                break;
            case "CompareRuns":
                SafeExecuteNavigationAsync(() => CompareRuns.LoadAvailableRunsAsync(), "CompareRuns");
                break;
            case "ArchivePlan":
                SafeExecuteNavigationAsync(() => ArchivePlan.LoadPlanAsync(), "ArchivePlan");
                break;
            case "ArchiveVerification":
                ArchiveVerification.Refresh();
                break;
        }
    }

    private async void SafeExecuteNavigationAsync(Func<Task> action, string targetView)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected on quick user navigation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background refresh failed when navigating to view: {ViewName}", targetView);
        }
    }
}
