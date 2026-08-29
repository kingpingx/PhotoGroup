using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Names that appear to belong to one person, and folding them into one.
/// </summary>
/// <remarks>
/// The counterpart to finding duplicate photographs, and the one the People screen actually needs:
/// grouping splits a person routinely, so anyone who has worked down a list naming groups has named
/// somebody twice. Nothing else in the application compares two named people, so without this the
/// only way to notice is to recognise the same face twice while scrolling.
///
/// Each set is merged on its own, with its own keeper, because the right name to keep is a judgement
/// that differs per set. A single button covering every set would be quicker and would make one
/// wrong keeper into a decision the user never got to see.
/// </remarks>
public sealed partial class DuplicatePeopleViewModel(
    FindDuplicatePeopleUseCase findDuplicates,
    MergePeopleUseCase mergePeople,
    IFaceRepository faces,
    IPhotoReader photos,
    ThumbnailLoader thumbnails) : ObservableObject
{
    public ObservableCollection<DuplicatePersonGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Nothing looked at yet.";

    [ObservableProperty]
    private bool _hasSearched;

    public string DetectorId { get; set; } = string.Empty;

    public string EmbedderId { get; set; } = string.Empty;

    /// <summary>Invoked after a merge, so the People screen rebuilds.</summary>
    public Func<Task>? Changed { get; set; }

    public string EmptyCaption => HasSearched
        ? "No two names look like the same person."
        : "Compare everyone already named, and find anybody named twice.";

    [RelayCommand]
    private async Task OpenAsync()
    {
        IsOpen = true;

        // Searched on opening rather than behind a second button. Comparing a few hundred averages
        // is instantaneous — unlike the photo comparison, nothing is decoded — so asking the user to
        // press again would only be ceremony.
        await FindAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task FindAsync()
    {
        IsBusy = true;
        FindCommand.NotifyCanExecuteChanged();

        try
        {
            var found = await findDuplicates
                .ExecuteAsync(DetectorId, FindDuplicatePeopleUseCase.DefaultMinimumSimilarity, CancellationToken.None)
                .ConfigureAwait(true);

            Groups.Clear();
            foreach (var group in found)
            {
                Groups.Add(new DuplicatePersonGroupViewModel(group, faces, photos, thumbnails));
            }

            HasSearched = true;
            OnPropertyChanged(nameof(EmptyCaption));

            Status = found.Count == 0
                ? "No two names look like the same person."
                : $"{found.Count:N0} set(s) of names look like one person each. "
                  + "Choose which name to keep, then merge.";
        }
        finally
        {
            IsBusy = false;
            FindCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsNotBusy() => !IsBusy;

    /// <summary>Folds one set into whichever of its names is marked to keep.</summary>
    [RelayCommand]
    private async Task MergeAsync(DuplicatePersonGroupViewModel? group)
    {
        if (group is null || IsBusy)
        {
            return;
        }

        var keeper = group.Members.FirstOrDefault(m => m.IsKeeper);
        if (keeper is null)
        {
            Status = "Choose which name to keep first.";
            return;
        }

        IsBusy = true;

        try
        {
            var result = await mergePeople
                .ExecuteAsync(
                    keeper.PersonId,
                    [.. group.Members.Where(m => !m.IsKeeper).Select(m => m.PersonId)],
                    DetectorId,
                    EmbedderId,
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                Status = result.Error!;
                return;
            }

            Status = $"Merged {result.MergedPeople:N0} name(s) into {result.Name}, "
                     + $"moving {result.MovedFaces:N0} photo(s).";

            Groups.Remove(group);
            OnPropertyChanged(nameof(EmptyCaption));

            if (Changed is { } changed)
            {
                await changed().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Makes one name the survivor of its set.</summary>
    [RelayCommand]
    private void ChooseKeeper(DuplicatePersonViewModel? person)
    {
        if (person is null)
        {
            return;
        }

        foreach (var group in Groups)
        {
            if (!group.Members.Contains(person))
            {
                continue;
            }

            foreach (var member in group.Members)
            {
                member.IsKeeper = ReferenceEquals(member, person);
            }

            group.Refresh();
            return;
        }
    }
}

/// <summary>One set of names that look like a single person.</summary>
public sealed partial class DuplicatePersonGroupViewModel : ObservableObject
{
    public DuplicatePersonGroupViewModel(
        DuplicatePersonGroup group,
        IFaceRepository faces,
        IPhotoReader photos,
        ThumbnailLoader thumbnails)
    {
        Members = [.. group.Members.Select((member, index) =>
            new DuplicatePersonViewModel(member, isKeeper: index == 0, faces, photos, thumbnails))];

        Refresh();
    }

    public ObservableCollection<DuplicatePersonViewModel> Members { get; }

    [ObservableProperty]
    private string _caption = string.Empty;

    [ObservableProperty]
    private string _action = string.Empty;

    public void Refresh()
    {
        var keeper = Members.FirstOrDefault(m => m.IsKeeper);
        var photos = Members.Sum(m => m.PhotoCount);

        Caption = $"{Members.Count:N0} names, {photos:N0} photo(s) between them";
        Action = keeper is null
            ? "Choose a name to keep"
            : $"Merge the other {Members.Count - 1:N0} into {keeper.Name}";
    }
}

/// <summary>One name within a set, with a face so the user can judge by the face.</summary>
public sealed partial class DuplicatePersonViewModel : ObservableObject
{
    private readonly IFaceRepository _faces;
    private readonly IPhotoReader _photos;
    private readonly ThumbnailLoader _thumbnails;
    private readonly FaceId? _coverFaceId;

    public DuplicatePersonViewModel(
        DuplicatePerson person,
        bool isKeeper,
        IFaceRepository faces,
        IPhotoReader photos,
        ThumbnailLoader thumbnails)
    {
        _faces = faces;
        _photos = photos;
        _thumbnails = thumbnails;
        _coverFaceId = person.CoverFaceId;

        PersonId = person.Id;
        Name = person.Name;
        FaceCount = person.FaceCount;
        PhotoCount = person.PhotoCount;
        _isKeeper = isKeeper;

        Caption = person.FaceCount == person.PhotoCount
            ? $"{person.PhotoCount:N0} photo(s)"
            : $"{person.FaceCount:N0} faces in {person.PhotoCount:N0} photo(s)";

        // Shown as a percentage because that is how sure this is, and the decision being asked of
        // the user is exactly how much to trust it. A cosine similarity would mean nothing here.
        Match = $"{person.Similarity:P0} alike";
    }

    public PersonId PersonId { get; }

    public string Name { get; }

    public int FaceCount { get; }

    public int PhotoCount { get; }

    public string Caption { get; }

    public string Match { get; }

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    [ObservableProperty]
    private bool _isKeeper;

    [ObservableProperty]
    private Bitmap? _face;

    public async void LoadFaceAsync()
    {
        if (Face is not null || _coverFaceId is not { } faceId)
        {
            return;
        }

        var found = await _faces.GetByIdsAsync([faceId], CancellationToken.None).ConfigureAwait(true);
        if (found.Count == 0)
        {
            return;
        }

        var photo = await _photos.GetByIdAsync(found[0].PhotoId, CancellationToken.None).ConfigureAwait(true);
        if (photo is null)
        {
            return;
        }

        Face = await _thumbnails
            .LoadFaceAsync(faceId, photo.Path, found[0].Box, CancellationToken.None)
            .ConfigureAwait(true);
    }
}
