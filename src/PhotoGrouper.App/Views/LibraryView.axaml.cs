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

    /// <summary>Opens the marked-up inspector for the clicked photo.</summary>
    private async void OnTilePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (Model is { } model && sender is Control { DataContext: PhotoTileViewModel tile })
        {
            await model.InspectCommand.ExecuteAsync(tile);
            SyncOverlayMarks();
        }
    }

    /// <remarks>
    /// The marks are pushed onto the drawing control rather than bound, because the source size
    /// has to be published alongside them. The two must change together: a mark list applied
    /// against the previous photo's dimensions draws boxes in the wrong places entirely.
    /// </remarks>
    private void SyncOverlayMarks()
    {
        if (Model is not { } model
            || this.FindControl<FaceOverlayControl>("OverlayMarks") is not { } marks)
        {
            return;
        }

        marks.SourceSize = model.Overlay.Image is { } image
            ? new Size(image.PixelSize.Width, image.PixelSize.Height)
            : default;

        marks.Marks = model.Overlay.Marks.ToArray();
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
