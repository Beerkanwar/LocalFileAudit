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
    private string _appVersion = "1.0.0 (Phase 1 Foundation)";

    [ObservableProperty]
    private bool _isSafeModeActive = true;

    public DashboardViewModel Dashboard { get; }
    public ScansViewModel Scans { get; }
    public InventoryViewModel Inventory { get; }
    public StorageAnalysisViewModel StorageAnalysis { get; }
    public DuplicateAnalysisViewModel DuplicateAnalysis { get; }
    public AiReportsViewModel AiReports { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        DashboardViewModel dashboard,
        ScansViewModel scans,
        InventoryViewModel inventory,
        StorageAnalysisViewModel storageAnalysis,
        DuplicateAnalysisViewModel duplicateAnalysis,
        AiReportsViewModel aiReports,
        SettingsViewModel settings,
        ILogger<MainViewModel> logger)
    {
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        Scans = scans ?? throw new ArgumentNullException(nameof(scans));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        StorageAnalysis = storageAnalysis ?? throw new ArgumentNullException(nameof(storageAnalysis));
        DuplicateAnalysis = duplicateAnalysis ?? throw new ArgumentNullException(nameof(duplicateAnalysis));
        AiReports = aiReports ?? throw new ArgumentNullException(nameof(aiReports));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentViewModel = Dashboard;
    }

    [RelayCommand]
    private void Navigate(string viewName)
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
            "Settings" => Settings,
            _ => Dashboard
        };
    }
}
