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
    private string _statusMessage = "Ready - Safe Read-Only Mode Active";

    [ObservableProperty]
    private string _appVersion = $"v{LocalFileAudit.Core.Common.AppVersion.Current}";

    [ObservableProperty]
    private bool _isSafeModeActive = true;

    public DashboardViewModel Dashboard { get; }
    public ScansViewModel Scans { get; }
    public InventoryViewModel Inventory { get; }
    public StorageAnalysisViewModel StorageAnalysis { get; }
    public DuplicateAnalysisViewModel DuplicateAnalysis { get; }
    public AiReportsViewModel AiReports { get; }
    public CompareRunsViewModel CompareRuns { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        DashboardViewModel dashboard,
        ScansViewModel scans,
        InventoryViewModel inventory,
        StorageAnalysisViewModel storageAnalysis,
        DuplicateAnalysisViewModel duplicateAnalysis,
        AiReportsViewModel aiReports,
        CompareRunsViewModel compareRuns,
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
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentViewModel = Dashboard;

        // Wire inter-viewModel navigation callbacks
        Dashboard.OnNavigateRequested = view => Navigate(view);
        Scans.OnNavigateToInventoryWithScan = async scanId =>
        {
            await Inventory.LoadInitialDataAsync(scanId);
            Navigate("Inventory");
        };
        Scans.OnNavigateToAnalysisWithScan = async scanId =>
        {
            await StorageAnalysis.LoadAnalysisAsync(scanId);
            Navigate("StorageAnalysis");
        };
        AiReports.OnNavigateToInventoryWithCandidate = async (candidateId, scanId) =>
        {
            Navigate("Inventory");
            await Inventory.SelectAndInspectFileByIdAsync(candidateId, scanId);
        };
    }

    [RelayCommand]
    public void Navigate(string viewName)
    {
        _logger.LogInformation("Navigating to view: {ViewName}", viewName);

        CurrentViewModel = viewName switch
        {
            "Dashboard" => Dashboard,
            "Scans" => Scans,
            "Inventory" => Inventory,
            "StorageAnalysis" => StorageAnalysis,
            "DuplicateAnalysis" => DuplicateAnalysis,
            "AiReports" => AiReports,
            "CompareRuns" => CompareRuns,
            "Settings" => Settings,
            _ => Dashboard
        };

        // Proactively refresh target view state
        switch (viewName)
        {
            case "Dashboard":
                _ = Dashboard.LoadLatestMetricsAsync();
                break;
            case "Scans":
                _ = Scans.RefreshScansAsync();
                break;
            case "Inventory":
                _ = Inventory.ExecuteSearchAsync();
                break;
            case "StorageAnalysis":
                _ = StorageAnalysis.RefreshGeneratedReportsAsync();
                break;
            case "CompareRuns":
                _ = CompareRuns.LoadAvailableRunsAsync();
                break;
        }
    }
}
