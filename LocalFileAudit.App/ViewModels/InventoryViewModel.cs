namespace LocalFileAudit.App.ViewModels;

public sealed class InventoryViewModel : ViewModelBase
{
    public InventoryViewModel()
    {
        Title = "File Inventory";
    }

    public string StatusMessage => "File inventory will be populated during filesystem scans. Virtualized search and filtering ready for Phase 2 & 4.";
}
