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
                .RemoveFacesAsync(_personId, selected, CancellationToken.None)
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

    public PersonPhotoViewModel(PersonPhoto photo, ThumbnailLoader thumbnails, Action onSelectionChanged)
    {
        _thumbnails = thumbnails;
        _onSelectionChanged = onSelectionChanged;
        _photoId = photo.PhotoId;
        _path = photo.Path;

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

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    public void Toggle() => IsSelected = !IsSelected;

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
