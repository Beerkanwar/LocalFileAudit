using CommunityToolkit.Mvvm.ComponentModel;

namespace LocalFileAudit.App.ViewModels;

/// <summary>
/// Base class for all MVVM view models.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
