namespace LocalFileAudit.App.ViewModels;

public sealed class ScansViewModel : ViewModelBase
{
    public ScansViewModel()
    {
        Title = "Scans";
    }

    public string StatusMessage => "No historical scans recorded yet. Scanner engine ready for Phase 2.";
}
