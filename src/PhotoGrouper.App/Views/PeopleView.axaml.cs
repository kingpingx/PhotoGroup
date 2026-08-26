using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class PeopleView : UserControl
{
    public PeopleView() => InitializeComponent();

    private PeopleViewModel? Model => DataContext as PeopleViewModel;

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (Model is { } model)
        {
            await model.RefreshAsync(CancellationToken.None);
        }
    }

    /// <remarks>
    /// Cover images load when a tile becomes visible rather than when the list is built. A library
    /// with hundreds of groups would otherwise decode hundreds of photographs before showing
    /// anything at all.
    /// </remarks>
    private void OnGroupAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: ClusterTileViewModel tile })
        {
            tile.LoadCoverAsync();
        }
    }

    /// <remarks>
    /// Naming is the action a user repeats most, so pressing Enter completes it. Reaching for the
    /// button every time would make working through a list of groups needlessly slow.
    /// </remarks>
    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is not { } model)
        {
            return;
        }

        if (sender is Control { DataContext: ClusterTileViewModel tile })
        {
            e.Handled = true;
            model.NameCommand.Execute(tile);
        }
    }

    /// <remarks>
    /// Hooked to the repeater's element lifecycle rather than to attachment, because a virtualising
    /// repeater reuses its containers: one attaches a single time and is then rebound to many
    /// different photographs without ever attaching again.
    /// </remarks>
    private void OnPersonPhotoPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is PersonPhotoViewModel photo)
        {
            photo.LoadThumbnailAsync();
        }
    }

    /// <summary>Clicking the dimmed area around the panel goes back.</summary>
    private void OnDetailBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model)
        {
            e.Handled = true;
            model.Detail.CloseCommand.Execute(null);
        }
    }

    /// <remarks>
    /// Stops a press inside the panel from reaching the background, which would otherwise close
    /// the panel every time somebody selected a photograph in it.
    /// </remarks>
    private void OnDetailContentPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>Clicking a photograph selects it, so several can be removed in one go.</summary>
    private void OnPersonPhotoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PersonPhotoViewModel photo })
        {
            e.Handled = true;
            photo.Toggle();
        }
    }
}
