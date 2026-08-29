using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class OrganiseView : UserControl
{
    public OrganiseView() => InitializeComponent();

    /// <remarks>
    /// The picker lives in the view rather than the view model because it belongs to this windowing
    /// toolkit, and a view model reaching for it would drag Avalonia into a layer meant to stay
    /// testable without a UI. The same reasoning as the folder pickers on the library screen.
    /// </remarks>
    private async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrganiseViewModel model || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to write the people into",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            model.OutputRoot = path;
        }
    }
}
