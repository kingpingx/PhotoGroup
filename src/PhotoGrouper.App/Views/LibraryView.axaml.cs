using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
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
    /// The picker lives here rather than in the view model for the same reason the folder picker
    /// above does: it belongs to this windowing toolkit, and a view model reaching for it drags
    /// Avalonia into a layer meant to stay testable without a UI.
    /// </remarks>
    private async void OnChooseQuarantineFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to move duplicates into",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            model.Duplicates.QuarantineFolder = path;
        }
    }

    /// <remarks>
    /// Thumbnails load as each tile appears, for the same reason the library grid does it: a set of
    /// duplicates can run to dozens, and decoding all of them before showing anything would make
    /// the panel appear to hang.
    /// </remarks>
    private void OnDuplicateAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: DuplicatePhotoViewModel photo })
        {
            photo.LoadThumbnailAsync();
        }
    }

    /// <summary>Clicking a photo chooses whether it moves.</summary>
    private void OnDuplicatePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DuplicatePhotoViewModel photo })
        {
            e.Handled = true;
            photo.Toggle();
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

    /// <summary>
    /// Dismisses the viewer when the dimmed area around the photograph is clicked.
    /// </summary>
    /// <remarks>
    /// The first thing anyone tries when a picture fills the window. Without it the only way out
    /// is a button somebody has to go looking for.
    /// </remarks>
    private void OnOverlayBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model)
        {
            e.Handled = true;
            model.CloseOverlayCommand.Execute(null);
        }
    }

    /// <summary>
    /// Stops a click on the photograph or the controls from reaching the background.
    /// </summary>
    /// <remarks>
    /// Without this the dismissal above would fire for every press inside the viewer, so pressing
    /// Next would close the thing being read.
    /// </remarks>
    private void OnOverlayContentPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>Sends the user to Settings, where clearing lives.</summary>
    /// <remarks>
    /// Deliberately navigates rather than clearing here. The action is irreversible, and Settings
    /// is where the summary of exactly what is about to be destroyed sits. Putting the destructive
    /// button itself beside Scan and Find faces would place it one slip away from the two controls
    /// used most.
    /// </remarks>
    private void OnClearLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel shell)
        {
            shell.ShowSettingsCommand.Execute(null);
        }
    }

    /// <remarks>
    /// Hooked to the repeater's own element lifecycle rather than to the tile's attachment to the
    /// visual tree. The two are not the same thing, and the difference is what left every tile
    /// blank: a virtualising repeater keeps a pool of containers and rebinds them to different
    /// items as the list scrolls, so a container attaches once and is then reused for many
    /// photographs without ever attaching again. Anything hung off attachment therefore runs for
    /// the first photograph a container ever shows and for none of the rest.
    ///
    /// ElementPrepared fires every time a container is bound to an item, with its data context
    /// already set, which is exactly when a thumbnail should start loading. ElementClearing fires
    /// when it is released, which is when an in-flight load for a photograph the user has already
    /// scrolled past should be abandoned.
    /// </remarks>
    private void OnTilePrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is PhotoTileViewModel tile)
        {
            tile.OnAttached();
        }
    }

    private void OnTileClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element.DataContext is PhotoTileViewModel tile)
        {
            tile.OnDetached();
        }
    }
}
