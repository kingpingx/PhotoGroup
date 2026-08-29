using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Everything a person can be corrected with: their photographs, their name, and their removal.
/// </summary>
/// <remarks>
/// Grouping by face gets things wrong, and until this existed there was no way to say so. A name
/// could be applied and never changed, a wrong grouping never undone, and a photograph of somebody
/// else never taken off a person. That made the automatic pass something to be endured rather than
/// corrected, which is the wrong way round.
/// </remarks>
public sealed partial class PersonDetailViewModel(
    ManagePeopleUseCase managePeople,
    FindDuplicateFacesUseCase findDuplicateFaces,
    ThumbnailLoader thumbnails) : ObservableObject
{
    private PersonId _personId;

    public ObservableCollection<PersonPhotoViewModel> Photos { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _personName = string.Empty;

    /// <summary>Bound to the rename box, so the stored name is not disturbed while typing.</summary>
    [ObservableProperty]
    private string _editedName = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Invoked after a change the People screen needs to reflect.
    /// </summary>
    /// <remarks>
    /// A settable callback rather than an event, because there is exactly one owner: the People
    /// screen creates this panel and is the only thing that needs to know. An event would suggest
    /// several listeners and leave the question of who unsubscribes.
    /// </remarks>
    public Func<Task>? Changed { get; set; }

    public string DetectorId { get; set; } = string.Empty;

    public string EmbedderId { get; set; } = string.Empty;

    /// <summary>Everybody else, so selected photographs can be moved to the right person.</summary>
    public ObservableCollection<PersonSummary> OtherPeople { get; } = [];

    public bool HasOtherPeople => OtherPeople.Count > 0;

    [ObservableProperty]
    private PersonSummary? _moveTarget;

    public string SelectionCaption => SelectedCount == 0
        ? "Click photos to select them"
        : $"Remove {SelectedCount:N0} selected";

    /// <summary>What the last search for near-identical faces found.</summary>
    [ObservableProperty]
    private string _duplicateSummary = string.Empty;

    public bool HasDuplicateSummary => !string.IsNullOrEmpty(DuplicateSummary);

    partial void OnDuplicateSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDuplicateSummary));

    public async Task OpenAsync(PersonId personId, string name, CancellationToken ct)
    {
        _personId = personId;
        PersonName = name;
        EditedName = name;
        Status = string.Empty;
        IsConfirmingDelete = false;
        IsOpen = true;

        await ReloadAsync(ct).ConfigureAwait(true);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        Photos.Clear();

        // Marks describe the tiles that were on screen, and the tiles are about to be rebuilt.
        // Leaving the summary behind would have it describing a set of photographs that no longer
        // exists, which is worst immediately after a removal, when it is most likely to be read.
        DuplicateSummary = string.Empty;

        OtherPeople.Clear();
        foreach (var person in await managePeople.GetOtherPeopleAsync(_personId, ct).ConfigureAwait(true))
        {
            OtherPeople.Add(person);
        }

        MoveTarget = null;
        OnPropertyChanged(nameof(HasOtherPeople));

        var found = await managePeople.GetPhotosAsync(_personId, DetectorId, ct).ConfigureAwait(true);
        foreach (var photo in found)
        {
            Photos.Add(new PersonPhotoViewModel(photo, thumbnails, OnSelectionChanged));
        }

        SelectedCount = 0;
        OnPropertyChanged(nameof(SelectionCaption));
    }

    private void OnSelectionChanged()
    {
        SelectedCount = Photos.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(SelectionCaption));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        Photos.Clear();
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task RenameAsync()
    {
        IsBusy = true;

        try
        {
            var result = await managePeople
                .RenameAsync(_personId, EditedName, CancellationToken.None)
                .ConfigureAwait(true);

            Status = result.Message;

            if (result.IsSuccess)
            {
                PersonName = EditedName.Trim();
                await NotifyChangedAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Takes the selected photographs off this person.
    /// </summary>
    /// <remarks>
    /// Recorded as a rejection, not merely cleared, so that the next grouping does not put them
    /// straight back and make the correction have to be repeated after every run.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync()
    {
        IsBusy = true;

        try
        {
            var selected = Photos.Where(p => p.IsSelected).Select(p => p.FaceId).ToList();

            var result = await managePeople
                .RemoveFacesAsync(_personId, selected, DetectorId, EmbedderId, CancellationToken.None)
                .ConfigureAwait(true);

            Status = result.Message;

            if (result.IsSuccess)
            {
                await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
                await NotifyChangedAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRemoveSelected() => !IsBusy && SelectedCount > 0;

    /// <summary>
    /// Marks the faces of this person that are the same moment, and selects the extras.
    /// </summary>
    /// <remarks>
    /// Deliberately not a separate screen. Everything needed to answer this is already on the one
    /// the user is looking at — every photograph, its face crop, a selection, and a button to remove
    /// what is selected — so the search annotates those tiles rather than opening a second grid of
    /// the same pictures and a second way to remove them.
    ///
    /// It selects rather than removes. The set is a suggestion, the tiles show which photographs it
    /// covers, and the existing remove button is what acts on it.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;

        try
        {
            var sets = await findDuplicateFaces
                .ExecuteAsync(
                    _personId,
                    DetectorId,
                    EmbedderId,
                    FindDuplicateFacesUseCase.DefaultMinimumSimilarity,
                    CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var photo in Photos)
            {
                photo.ClearDuplicateMark();
            }

            var byFace = Photos.ToDictionary(photo => photo.FaceId);
            var setNumber = 0;
            var extras = 0;

            foreach (var set in sets)
            {
                setNumber++;

                foreach (var member in set.Members)
                {
                    if (!byFace.TryGetValue(member.FaceId, out var tile))
                    {
                        continue;
                    }

                    var isKeeper = member.FaceId == set.Keeper.FaceId;
                    tile.MarkDuplicate(setNumber, isKeeper, member.Similarity);

                    // Everything but the one worth keeping arrives selected, because that is what
                    // asking for duplicates means. Nothing is removed until the button is pressed.
                    tile.SetSelected(!isKeeper);

                    if (!isKeeper)
                    {
                        extras++;
                    }
                }
            }

            OnSelectionChanged();

            DuplicateSummary = sets.Count == 0
                ? "No near-identical faces."
                : $"{sets.Count:N0} set(s) of near-identical faces, {extras:N0} extra selected.";

            Status = DuplicateSummary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Moves the selected photographs to somebody else.
    /// </summary>
    /// <remarks>
    /// The correction for a face that grouping put on the wrong person, as opposed to one that is
    /// nobody in this library. Removing would only detach it and leave it to be grouped again;
    /// moving says where it actually belongs.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private async Task MoveSelectedAsync()
    {
        if (MoveTarget is not { } target)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var selected = Photos.Where(p => p.IsSelected).Select(p => p.FaceId).ToList();

            var result = await managePeople
                .MoveFacesAsync(_personId, target.Id, selected, DetectorId, EmbedderId, CancellationToken.None)
                .ConfigureAwait(true);

            Status = result.Message;

            if (result.IsSuccess)
            {
                await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
                await NotifyChangedAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMoveSelected() => !IsBusy && SelectedCount > 0 && MoveTarget is not null;

    partial void OnMoveTargetChanged(PersonSummary? value) =>
        MoveSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var photo in Photos)
        {
            photo.IsSelected = true;
        }

        OnSelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var photo in Photos)
        {
            photo.IsSelected = false;
        }

        OnSelectionChanged();
    }

    [RelayCommand]
    private void BeginDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ConfirmDeleteAsync()
    {
        IsBusy = true;
        IsConfirmingDelete = false;

        try
        {
            var result = await managePeople
                .DeleteAsync(_personId, DetectorId, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess)
            {
                IsOpen = false;
                Photos.Clear();
                await NotifyChangedAsync().ConfigureAwait(true);
            }

            Status = result.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsNotBusy() => !IsBusy;

    private async Task NotifyChangedAsync()
    {
        if (Changed is { } handler)
        {
            await handler().ConfigureAwait(true);
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
        ConfirmDeleteCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One photograph a person appears in, selectable for removal.</summary>
public sealed partial class PersonPhotoViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnails;
    private readonly Action _onSelectionChanged;
    private readonly PhotoId _photoId;
    private readonly string _path;
    private readonly FaceBox _box;

    public PersonPhotoViewModel(PersonPhoto photo, ThumbnailLoader thumbnails, Action onSelectionChanged)
    {
        _thumbnails = thumbnails;
        _onSelectionChanged = onSelectionChanged;
        _photoId = photo.PhotoId;
        _path = photo.Path;
        _box = photo.Box;

        FaceId = photo.FaceId;
        FileName = System.IO.Path.GetFileName(photo.Path);
        WasConfirmed = photo.Assignment == Assignment.Confirmed;
    }

    public FaceId FaceId { get; }

    public string FileName { get; }

    /// <summary>True when the user has already vouched for this one, rather than the app guessing.</summary>
    public bool WasConfirmed { get; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>
    /// The face this tile is really about, cut out of the photograph.
    /// </summary>
    /// <remarks>
    /// A person can hold two faces from one photograph — grouping occasionally decides a stranger
    /// in the background is them — and the same picture then appears twice with nothing to say
    /// which tile removes which face. With the crop, the wrong one is obvious and can be taken off.
    /// </remarks>
    [ObservableProperty]
    private Bitmap? _faceCrop;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_isBulkChanging)
        {
            _onSelectionChanged();
        }
    }

    private bool _isBulkChanging;

    /// <summary>
    /// Sets the flag without announcing it, for the search that sets many at once.
    /// </summary>
    /// <remarks>
    /// Each announcement re-counts every tile, so letting a search over a hundred photographs
    /// announce per tile costs the square of their number. The caller announces once at the end.
    /// </remarks>
    public void SetSelected(bool selected)
    {
        _isBulkChanging = true;
        try
        {
            IsSelected = selected;
        }
        finally
        {
            _isBulkChanging = false;
        }
    }

    /// <summary>Which set of near-identical faces this tile belongs to, if any.</summary>
    [ObservableProperty]
    private string _duplicateBadge = string.Empty;

    public bool IsInDuplicateSet => !string.IsNullOrEmpty(DuplicateBadge);

    partial void OnDuplicateBadgeChanged(string value) =>
        OnPropertyChanged(nameof(IsInDuplicateSet));

    /// <summary>True when this is the one of its set suggested for keeping.</summary>
    [ObservableProperty]
    private bool _isDuplicateKeeper;

    /// <summary>How alike this face is to the nearest other in its set.</summary>
    [ObservableProperty]
    private string _matchCaption = string.Empty;

    public void MarkDuplicate(int setNumber, bool isKeeper, float similarity)
    {
        DuplicateBadge = $"SET {setNumber}";
        IsDuplicateKeeper = isKeeper;

        // A percentage, because what the user is being asked is how much to trust this. A cosine
        // similarity on a tile would mean nothing to anybody.
        MatchCaption = $"{similarity:P0} alike";
    }

    public void ClearDuplicateMark()
    {
        DuplicateBadge = string.Empty;
        IsDuplicateKeeper = false;
        MatchCaption = string.Empty;
    }

    public void Toggle() => IsSelected = !IsSelected;

    public async void LoadThumbnailAsync()
    {
        if (Thumbnail is null)
        {
            Thumbnail = await _thumbnails
                .LoadAsync(_photoId, _path, CancellationToken.None)
                .ConfigureAwait(true);
        }

        if (FaceCrop is null)
        {
            FaceCrop = await _thumbnails
                .LoadFaceAsync(FaceId, _path, _box, CancellationToken.None)
                .ConfigureAwait(true);
        }
    }
}
