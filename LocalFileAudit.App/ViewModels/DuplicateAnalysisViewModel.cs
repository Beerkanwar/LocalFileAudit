namespace LocalFileAudit.App.ViewModels;

public sealed class DuplicateAnalysisViewModel : ViewModelBase
{
    public DuplicateAnalysisViewModel()
    {
        Title = "Duplicate Analysis";
    }

    public string SafetyNotice => "Notice: Duplicate detection identifies identical content across files, but never proves that removing one path is safe. LocalFileAudit never deletes or moves files automatically.";
    public string StatusMessage => "Duplicate group analysis with optional two-stage SHA-256 hashing will display here in Phase 6.";
}
