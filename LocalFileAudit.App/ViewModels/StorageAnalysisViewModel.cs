namespace LocalFileAudit.App.ViewModels;

public sealed class StorageAnalysisViewModel : ViewModelBase
{
    public StorageAnalysisViewModel()
    {
        Title = "Storage Analysis";
    }

    public string StatusMessage => "Deterministic storage aggregations, large file ranking, and extension breakdowns will display here.";
}
