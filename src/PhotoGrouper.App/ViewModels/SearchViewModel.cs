using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// The screen that answers "show me every photo of Alice".
/// </summary>
/// <remarks>
/// Everything before this finds and measures faces and turns them into named people. This is where
/// that work pays for itself, and it is the only screen that can be asked about more than one
/// person at once — a person's own page cannot be asked for the two of them together.
///
/// Nothing here changes anything. It is the one screen in the application that only reads, which is
/// why it has no confirmations, no busy guards on destructive actions, and no notifier: there is
/// nothing it can leave in a state anybody has to recover from.
/// </remarks>
public sealed partial class SearchViewModel(
    SearchPhotosUseCase search,
    IPersonRepository people,
    ThumbnailLoader thumbnails,
    LibraryChangedNotifier libraryChanged) : ObservableObject
{
    private bool _subscribed;

    /// <summary>Everybody who has been named, to search among.</summary>
    public ObservableCollection<SearchPersonViewModel> People { get; } = [];

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>
    /// True for photographs holding everybody chosen, false for photographs holding any of them.
    /// </summary>
    /// <remarks>
    /// Two genuinely different questions, and the difference is the reason to choose more than one
    /// person: everybody is what somebody means by a photograph of the two of them, anybody is what
    /// they mean by everything from the holiday.
    /// </remarks>
    [ObservableProperty]
    private bool _matchAll = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasSearched;

    public int ChosenCount => People.Count(person => person.IsChosen);

    public string EmptyCaption => HasSearched
        ? "Nothing matched. Try fewer people, or \"any of them\" instead of \"all of them\"."
        : "Choose somebody, or type part of a file name, then press Search.";

    public string DetectorId { get; set; } = DetectorRegistry.DefaultDetectorId;

    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSubscribed();

        // The chosen set is kept across a refresh. Renaming somebody, or naming a new person,
        // should not silently empty a search the user is part way through building.
        var chosen = People.Where(p => p.IsChosen).Select(p => p.Id).ToHashSet();

        People.Clear();

        foreach (var person in (await people.GetAllAsync(ct).ConfigureAwait(true))
                     .OrderBy(person => person.Name.Value, NaturalStringComparer.Instance))
        {
            People.Add(new SearchPersonViewModel(person.Id, person.Name.Value, OnChosenChanged)
            {
                IsChosen = chosen.Contains(person.Id),
            });
        }

        OnPropertyChanged(nameof(ChosenCount));
        SearchCommand.NotifyCanExecuteChanged();
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        libraryChanged.Subscribe(() => RefreshAsync(CancellationToken.None));
    }

    private void OnChosenChanged()
    {
        OnPropertyChanged(nameof(ChosenCount));
        SearchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsBusy = true;

        try
        {
            var results = await Task.Run(
                () => search.ExecuteAsync(
                    new SearchQuery(
                        [.. People.Where(p => p.IsChosen).Select(p => p.Id)],
                        MatchAll,
                        string.IsNullOrWhiteSpace(Text) ? null : Text),
                    DetectorId,
                    CancellationToken.None))
                .ConfigureAwait(true);

            Results.Clear();
            foreach (var hit in results.Hits)
            {
                Results.Add(new SearchResultViewModel(hit, thumbnails));
            }

            HasSearched = true;
            OnPropertyChanged(nameof(EmptyCaption));

            Status = results.TotalMatched == 0
                ? "Nothing matched."
                : results.Truncated
                    ? $"{results.TotalMatched:N0} photo(s) matched. Showing the first "
                      + $"{results.Hits.Count:N0}; narrow the search to see the rest."
                    : $"{results.TotalMatched:N0} photo(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearch() => !IsBusy && (ChosenCount > 0 || !string.IsNullOrWhiteSpace(Text));

    partial void OnTextChanged(string value) => SearchCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => SearchCommand.NotifyCanExecuteChanged();

    partial void OnMatchAllChanged(bool value) => OnPropertyChanged(nameof(ChosenCount));

    [RelayCommand]
    private void Clear()
    {
        foreach (var person in People)
        {
            person.SetChosen(false);
        }

        Text = string.Empty;
        Results.Clear();
        HasSearched = false;
        Status = string.Empty;

        OnChosenChanged();
        OnPropertyChanged(nameof(EmptyCaption));
    }
}

/// <summary>One named person, and whether this search is about them.</summary>
public sealed partial class SearchPersonViewModel(
    PersonId id, string name, Action onChosenChanged) : ObservableObject
{
    private bool _isBulkChanging;

    public PersonId Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isChosen;

    partial void OnIsChosenChanged(bool value)
    {
        if (!_isBulkChanging)
        {
            onChosenChanged();
        }
    }

    /// <summary>Sets the flag without announcing it, for clearing the whole list at once.</summary>
    public void SetChosen(bool chosen)
    {
        _isBulkChanging = true;
        try
        {
            IsChosen = chosen;
        }
        finally
        {
            _isBulkChanging = false;
        }
    }

    public void Toggle() => IsChosen = !IsChosen;
}

/// <summary>One photograph a search found, and why it matched.</summary>
public sealed partial class SearchResultViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnails;
    private readonly PhotoId _photoId;
    private readonly string _path;

    public SearchResultViewModel(SearchHit hit, ThumbnailLoader thumbnails)
    {
        _thumbnails = thumbnails;
        _photoId = hit.Photo.Id;
        _path = hit.Photo.Path;

        FileName = System.IO.Path.GetFileName(hit.Photo.Path);
        Folder = System.IO.Path.GetDirectoryName(hit.Photo.Path) ?? string.Empty;

        Taken = (hit.Photo.TakenUtc ?? hit.Photo.ModifiedUtc).LocalDateTime.ToString("d MMM yyyy");

        // Named rather than counted. "Alice, Bob" answers why this photograph is in the results;
        // "2 people" leaves the reader to open it and find out.
        People = hit.PeopleInPhoto.Count == 0
            ? "nobody named"
            : string.Join(", ", hit.PeopleInPhoto);
    }

    public string FileName { get; }

    public string Folder { get; }

    public string Taken { get; }

    public string People { get; }

    public string FullPath => _path;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public async void LoadThumbnailAsync()
    {
        if (Thumbnail is null)
        {
            Thumbnail = await _thumbnails
                .LoadAsync(_photoId, _path, CancellationToken.None)
                .ConfigureAwait(true);
        }
    }
}
