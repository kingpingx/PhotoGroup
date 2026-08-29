using Avalonia.Controls;
using Avalonia.Input;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();

    /// <remarks>
    /// Thumbnails load as each tile is prepared rather than when the results arrive, for the same
    /// reason the library grid does it: a search may return five hundred photographs and decoding
    /// all of them before showing anything would make the screen appear to hang.
    /// </remarks>
    private void OnResultPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is SearchResultViewModel result)
        {
            result.LoadThumbnailAsync();
        }
    }

    /// <remarks>
    /// Enter searches, because the box is the last thing touched before wanting an answer and
    /// reaching for the button every time would make refining a search slower than it needs to be.
    /// </remarks>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SearchViewModel model)
        {
            e.Handled = true;
            model.SearchCommand.Execute(null);
        }
    }
}
