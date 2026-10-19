namespace LocalFileAudit.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    public DashboardViewModel()
    {
        Title = "Dashboard";
    }

    public string TotalFilesDiscoveredText => "0 files";
    public string TotalLogicalSizeText => "0.00 GB";
    public string DrivesCountText => "0 drives";
    public string ScanStateText => "Idle - Ready to scan";
    public string LastScanTimeText => "Never";
}
