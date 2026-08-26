using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoGrouper.App.ViewModels;

/// <summary>Shell view model. Holds the sections the left navigation switches between.</summary>
public sealed partial class MainWindowViewModel(LibraryViewModel library) : ObservableObject
{
    public LibraryViewModel Library { get; } = library;

    [ObservableProperty]
    private string _title = "PhotoGrouper";
}
