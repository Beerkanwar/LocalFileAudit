namespace LocalFileAudit.App.ViewModels;

public sealed class AiReportsViewModel : ViewModelBase
{
    public AiReportsViewModel()
    {
        Title = "AI Reports";
    }

    public string AiPrivacyNotice => "Privacy Guarantee: AI integration uses local Ollama instances on localhost only. No file contents or personal files are transmitted to the cloud or external services.";
    public string StatusMessage => "Local AI inference is currently disabled. Deterministic reports remain 100% functional without AI.";
}
