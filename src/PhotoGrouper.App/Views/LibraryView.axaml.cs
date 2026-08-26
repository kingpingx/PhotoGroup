using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private LibraryViewModel? Model => DataContext as LibraryViewModel;

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (Model is { } model)
        {
            await model.InitializeAsync(CancellationToken.None);
        }
    }

    /// <remarks>
    /// The folder picker lives in the view rather than the view model because it belongs to
    /// this windowing toolkit. A view model reaching for it would drag Avalonia into a layer
    /// that is meant to stay testable without a UI.
    /// </remarks>
    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder of photos",
            AllowMultiple = true,
        });

        foreach (var folder in folders)
        {
            if (folder.TryGetLocalPath() is { } path)
            {
                await model.AddFolderAsync(path, CancellationToken.None);
            }
        }
    }

    /// <remarks>
    /// The virtualising panel recycles containers as they scroll, so a tile is told when it
    /// becomes visible and when it is taken away again. That second notification is what
    /// cancels an in-flight decode for a photo the user has already scrolled past.
    /// </remarks>
    private void OnTileAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: PhotoTileViewModel tile })
        {
            tile.OnAttached();
        }
    }

    private void OnTileDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: PhotoTileViewModel tile })
        {
            tile.OnDetached();
        }
    }
}
