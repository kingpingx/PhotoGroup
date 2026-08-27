using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// The People screen: groups awaiting a name, and the people already named.
/// </summary>
/// <remarks>
/// The point of the whole application. Everything before this finds and measures faces; here a
/// user types one name and every photograph of that person becomes findable at once.
/// </remarks>
public sealed partial class PeopleViewModel(
    IClusterRepository clusters,
    IPersonRepository people,
    IFaceRepository faces,
    IPhotoReader photos,
    EmbedFacesUseCase embedFaces,
    ClusterFacesUseCase clusterFaces,
    NamePersonUseCase namePerson,
    IgnoreGroupUseCase ignoreGroup,
    ModelStore models,
    OnnxSessionFactory sessions,
    ThumbnailLoader thumbnails,
    LibraryChangedNotifier libraryChanged,
    PersonDetailViewModel detail) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private bool _subscribed;

    /// <summary>
    /// Registers for library-wide changes the first time this screen loads.
    /// </summary>
    /// <remarks>
    /// Done here rather than in the constructor because the subscription calls back into a refresh
    /// that reads storage, and a constructor running during dependency resolution is not a place
    /// to start that.
    /// </remarks>
    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Detail.DetectorId = DetectorId;
        libraryChanged.Subscribe(() =>
        {
            Detail.CloseCommand.Execute(null);
            return RefreshAsync(CancellationToken.None);
        });
    }

    /// <summary>Groups of two or more faces awaiting a name, largest first.</summary>
    public ObservableCollection<ClusterTileViewModel> UnnamedGroups { get; } = [];

    /// <summary>
    /// People who appear exactly once.
    /// </summary>
    /// <remarks>
    /// Shown apart from the rest rather than mixed in. A single face carries no corroboration, so
    /// these are the least certain groupings and are the most likely to be strangers in the
    /// background. Kept visible all the same: they used to be discarded, which on a small library
    /// silently hid a third of the faces, several of them large and confident.
    /// </remarks>
    public ObservableCollection<ClusterTileViewModel> SingleAppearances { get; } = [];

    /// <summary>
    /// How many one-off groups are listed at once.
    /// </summary>
    /// <remarks>
    /// A large library can contain thousands of people photographed once. Showing every one would
    /// bury the groups that matter and cost a thumbnail decode each; the count of the remainder is
    /// reported instead.
    /// </remarks>
    private const int MaximumSingleAppearancesShown = 60;

    public ObservableCollection<PersonTileViewModel> NamedPeople { get; } = [];

    [ObservableProperty]
    private string _status = "Run grouping to find people.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _detectorId = DetectorRegistry.DefaultDetectorId;

    /// <summary>Counts shown in the workflow header.</summary>
    [ObservableProperty]
    private int _unnamedGroupCount;

    [ObservableProperty]
    private int _namedPeopleCount;

    public bool HasGroups => UnnamedGroupCount > 0 || NamedPeopleCount > 0 || SingleAppearanceCount > 0;

    /// <summary>How many faces have been dismissed, so they can be brought back.</summary>
    [ObservableProperty]
    private int _ignoredFaceCount;

    [ObservableProperty]
    private int _singleAppearanceCount;

    /// <summary>One-off groups beyond the display cap, reported rather than silently omitted.</summary>
    [ObservableProperty]
    private int _hiddenSingleAppearanceCount;

    public string EmbedderId => ArcFaceEmbedder.Provider.Id;

    /// <summary>How the named people are ordered.</summary>
    public IReadOnlyList<PersonSort> SortOptions { get; } = PersonSort.All;

    [ObservableProperty]
    private PersonSort _selectedSort = PersonSort.All[0];

    partial void OnSelectedSortChanged(PersonSort value) => ApplySort();

    /// <remarks>
    /// Sorts the existing collection in place rather than reloading. Re-reading from storage would
    /// count every person's photographs again to answer a question about presentation.
    /// </remarks>
    private void ApplySort()
    {
        var ordered = SelectedSort.Apply(NamedPeople).ToList();

        NamedPeople.Clear();
        foreach (var person in ordered)
        {
            NamedPeople.Add(person);
        }
    }

    /// <summary>The panel for correcting one person: rename, remove photos, or remove them.</summary>
    public PersonDetailViewModel Detail { get; } = detail;

    /// <summary>Opens a named person for review and correction.</summary>
    [RelayCommand]
    private async Task OpenPersonAsync(PersonTileViewModel? person)
    {
        if (person is null)
        {
            return;
        }

        Detail.DetectorId = DetectorId;
        Detail.EmbedderId = EmbedderId;
        Detail.Changed = () => RefreshAsync(CancellationToken.None);
        await Detail.OpenAsync(person.Id, person.Name, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSubscribed();

        var records = await clusters.GetAllAsync(DetectorId, EmbedderId, ct).ConfigureAwait(true);
        var named = await people.GetAllAsync(ct).ConfigureAwait(true);

        // Built before the group tiles, because each of them offers this list. These carry no
        // cover face: they exist only to fill a drop-down, and loading a crop for each would
        // decode a photograph per named person on every refresh to show a list of names.
        var knownPeople = named
            .Select(person => new PersonTileViewModel(person.Id, person.Name.Value, 0, thumbnails))
            .OrderBy(person => person.Name, NaturalStringComparer.Instance)
            .ToList();

        UnnamedGroups.Clear();
        NamedPeople.Clear();

        SingleAppearances.Clear();

        // Largest groups first, so the people worth naming are reached before the long tail of
        // one-off strangers. Storage returns them in that order already; sorting here means the
        // screen does not depend on a query's ordering to stay sensible.
        foreach (var record in records
                     .Where(r => r.PersonId is null)
                     .OrderByDescending(r => r.Size)
                     .ThenBy(r => r.Id.Value))
        {
            var tile = new ClusterTileViewModel(record, thumbnails, faces, photos)
            {
                KnownPeople = knownPeople,
            };

            if (record.Size >= ClusterFacesUseCase.MinimumClusterSize)
            {
                UnnamedGroups.Add(tile);
            }
            else if (SingleAppearances.Count < MaximumSingleAppearancesShown)
            {
                SingleAppearances.Add(tile);
            }
        }

        SingleAppearanceCount = records.Count(r => r.PersonId is null && r.Size < ClusterFacesUseCase.MinimumClusterSize);
        HiddenSingleAppearanceCount = Math.Max(0, SingleAppearanceCount - SingleAppearances.Count);

        foreach (var person in named)
        {
            var assigned = await faces.GetByPersonAsync(person.Id, DetectorId, ct).ConfigureAwait(true);

            NamedPeople.Add(new PersonTileViewModel(person.Id, person.Name.Value, assigned.Count, thumbnails)
            {
                Cover = await ResolveCoverAsync(person, assigned, ct).ConfigureAwait(true),
            });
        }

        ApplySort();

        UnnamedGroupCount = UnnamedGroups.Count;
        NamedPeopleCount = NamedPeople.Count;
        IgnoredFaceCount = await ignoreGroup.CountAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(SingleAppearanceCaption));

        if (records.Count == 0 && named.Count == 0)
        {
            Status = "No groups yet. Scan and detect faces first, then run grouping.";
        }
        else
        {
            Status = $"{UnnamedGroups.Count:N0} group(s) awaiting a name, {NamedPeople.Count:N0} person/people named.";
        }
    }

    /// <summary>
    /// Picks the face to show on a person's tile, and finds the photograph it lives in.
    /// </summary>
    /// <remarks>
    /// A name on its own does not answer the question the tile exists to answer. Somebody working
    /// through a library has just named several groups in a row and needs to see, at a glance,
    /// which face each name went to — otherwise checking a name means opening it, and correcting a
    /// mistake means opening all of them.
    ///
    /// The person's chosen cover is preferred where they have one. Failing that, the largest face
    /// they appear in: a face that fills the frame is a portrait, and a small one is somebody
    /// caught in the background of a photograph of somebody else. Score breaks a tie, because two
    /// faces the same size are distinguished by how sure the detector was.
    /// </remarks>
    private async Task<FaceCoverViewModel?> ResolveCoverAsync(
        Person person, IReadOnlyList<Face> assigned, CancellationToken ct)
    {
        if (assigned.Count == 0)
        {
            return null;
        }

        var chosen = assigned.FirstOrDefault(face => face.Id == person.CoverFaceId)
                     ?? assigned
                         .OrderByDescending(face => face.Box.SmallestSide)
                         .ThenByDescending(face => face.Box.Score)
                         .First();

        var photo = await photos.GetByIdAsync(chosen.PhotoId, ct).ConfigureAwait(true);
        return photo is null ? null : new FaceCoverViewModel(chosen.Id, photo.Path, chosen.Box);
    }

    /// <summary>
    /// Embeds every outstanding face and regroups the library.
    /// </summary>
    /// <remarks>
    /// Presented as one action because the two stages are meaningless apart: embedding without
    /// grouping produces nothing a user can see, and grouping without embedding has nothing to
    /// work from. They remain separate use cases so that each is resumable on its own.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task GroupAsync()
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        IsProgressIndeterminate = true;
        GroupCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        var progress = new DelegateProgressSink(update =>
        {
            Status = update.Total is { } total
                ? $"{update.Stage}: {update.Completed:N0} of {total:N0}"
                : $"{update.Stage}: {update.Completed:N0}";

            if (update.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                ProgressFraction = fraction;
            }
        });

        try
        {
            Status = "Preparing the recognition model...";

            var download = new Progress<double>(fraction =>
            {
                IsProgressIndeterminate = false;
                ProgressFraction = fraction;
                Status = $"Downloading the recognition model... {fraction:P0} of 174 MB";
            });

            var modelPath = await models
                .EnsureAvailableAsync(ArcFaceEmbedder.Model, download, cancellation.Token)
                .ConfigureAwait(true);

            IsProgressIndeterminate = true;
            using var embedder = ArcFaceEmbedder.Load(modelPath, sessions);

            var embedded = await Task.Run(
                () => embedFaces.ExecuteAsync(embedder, DetectorId, progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            IsProgressIndeterminate = true;
            var grouped = await Task.Run(
                () => clusterFaces.ExecuteAsync(
                    DetectorId,
                    embedder.Info.Id,
                    ClusterFacesUseCase.DefaultSimilarityThreshold,
                    progress,
                    cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);

            Status =
                $"Found {grouped.ClustersFormed:N0} group(s) across {grouped.FacesGrouped:N0} face(s)"
                + (grouped.PeopleRecognised > 0
                    ? $", {grouped.PeopleRecognised:N0} matched to people you have already named"
                    : string.Empty)
                + (embedded.FacesEmbedded > 0 ? $", {embedded.FacesEmbedded:N0} newly recognised" : string.Empty)
                + (grouped.FacesUnsorted > 0 ? $", {grouped.FacesUnsorted:N0} unmatched" : string.Empty)
                + ".";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Recognised faces have been saved and will not be redone.";
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ModelUnavailableException e)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressFraction = 0;
            _cancellation = null;
            cancellation.Dispose();
            GroupCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsNotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    public string SingleAppearanceCaption => HiddenSingleAppearanceCount > 0
        ? $"APPEARS ONCE — showing {SingleAppearances.Count:N0} of {SingleAppearanceCount:N0}"
        : "APPEARS ONCE";

    /// <summary>
    /// Adds a group to somebody already named, instead of naming it afresh.
    /// </summary>
    /// <remarks>
    /// Routed through the same operation as naming, which already treats an existing name as a
    /// request to merge. Writing a separate path would mean a second place that has to remember to
    /// update the person's average vector and to leave the user's own corrections alone.
    /// </remarks>
    [RelayCommand]
    private async Task AssignToExistingAsync(ClusterTileViewModel? tile)
    {
        if (tile?.SelectedPerson is not { } person)
        {
            return;
        }

        var result = await namePerson.ExecuteAsync(tile.ClusterId, person.Name, CancellationToken.None)
            .ConfigureAwait(true);

        Status = result.IsSuccess
            ? $"Added {result.FacesAssigned:N0} photo(s) to {result.Name}."
            : result.Error!;

        if (result.IsSuccess)
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Dismisses a group of faces the user does not want to name.</summary>
    /// <remarks>
    /// Most faces in a library belong to strangers. Without this the only way to clear a group off
    /// the screen is to invent a name for somebody nobody cares about.
    /// </remarks>
    [RelayCommand]
    private async Task IgnoreAsync(ClusterTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var dismissed = await ignoreGroup.ExecuteAsync(tile.ClusterId, CancellationToken.None)
            .ConfigureAwait(true);

        Status = $"Dismissed {dismissed:N0} face(s). They will not be grouped again.";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Brings every dismissed face back.</summary>
    [RelayCommand]
    private async Task RestoreIgnoredAsync()
    {
        await ignoreGroup.RestoreAllAsync(CancellationToken.None).ConfigureAwait(true);
        Status = "Dismissed faces restored. Group faces again to see them.";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Names a group, which is the moment the app becomes useful.</summary>
    [RelayCommand]
    private async Task NameAsync(ClusterTileViewModel? tile)
    {
        if (tile is null || string.IsNullOrWhiteSpace(tile.ProposedName))
        {
            return;
        }

        var result = await namePerson.ExecuteAsync(tile.ClusterId, tile.ProposedName, CancellationToken.None)
            .ConfigureAwait(true);

        Status = result.IsSuccess
            ? (result.Merged
                  ? $"Added {result.FacesAssigned:N0} more photo(s) to {result.Name}."
                  : $"{result.Name} now has {result.FacesAssigned:N0} photo(s).")
              + (result.GroupsAbsorbed > 0
                  ? $" {result.GroupsAbsorbed:N0} other group(s) were recognised as the same person."
                  : string.Empty)
            : result.Error!;

        if (result.IsSuccess)
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private sealed class DelegateProgressSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(ProgressUpdate update) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onReport(update));
    }
}

/// <summary>An unnamed group, with a cover face and a box to name it in.</summary>
public sealed partial class ClusterTileViewModel(
    ClusterRecord record,
    ThumbnailLoader thumbnails,
    IFaceRepository faces,
    IPhotoReader photos) : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _cover;

    /// <summary>
    /// The one face this group is about, cut out of the photograph above it.
    /// </summary>
    /// <remarks>
    /// The whole photograph alone is ambiguous the moment it contains more than one person: a
    /// group shot of three produces three tiles showing the identical picture, and nothing on any
    /// of them says which of the three is being asked about. Shown beside the photograph rather
    /// than instead of it, so the context the photograph carries is not lost to gain the answer.
    /// </remarks>
    [ObservableProperty]
    private Bitmap? _faceCrop;

    [ObservableProperty]
    private string _proposedName = string.Empty;

    public ClusterId ClusterId { get; } = record.Id;

    /// <summary>
    /// People already named, offered so this group can join one of them.
    /// </summary>
    /// <remarks>
    /// Grouping cannot always tell that two groups are one person, and typing the name again is a
    /// poor substitute: it depends on spelling it identically, and offers no reminder of who has
    /// already been named. Choosing from the list removes both problems.
    /// </remarks>
    public IReadOnlyList<PersonTileViewModel> KnownPeople { get; init; } = [];

    public bool HasKnownPeople => KnownPeople.Count > 0;

    [ObservableProperty]
    private PersonTileViewModel? _selectedPerson;

    public int Size { get; } = record.Size;

    public string Caption { get; } = $"{record.Size} photo(s)";

    /// <remarks>
    /// Shows the whole photograph the group's most central face came from, not the face alone.
    /// Cropping to the face would be tidier but strips the context a person uses to recognise who
    /// they are looking at, which is the only thing being asked of them here.
    /// </remarks>
    public async void LoadCoverAsync()
    {
        if (Cover is not null)
        {
            return;
        }

        var members = await faces.GetByClusterAsync(ClusterId, CancellationToken.None).ConfigureAwait(true);
        var medoid = members.FirstOrDefault(f => f.Id == record.MedoidFaceId) ?? members.FirstOrDefault();
        if (medoid is null)
        {
            return;
        }

        var photo = await photos.GetByIdAsync(medoid.PhotoId, CancellationToken.None).ConfigureAwait(true);
        if (photo is null)
        {
            return;
        }

        Cover = await thumbnails.LoadAsync(photo.Id, photo.Path, CancellationToken.None).ConfigureAwait(true);

        FaceCrop = await thumbnails
            .LoadFaceAsync(medoid.Id, photo.Path, medoid.Box, CancellationToken.None)
            .ConfigureAwait(true);
    }
}

/// <summary>
/// An order the People screen can be shown in.
/// </summary>
/// <remarks>
/// Modelled as objects rather than an enum plus a switch, so that each ordering carries its own
/// comparison and adding one means adding an entry rather than editing a statement elsewhere.
/// </remarks>
public sealed record PersonSort(string Name, Func<IEnumerable<PersonTileViewModel>, IEnumerable<PersonTileViewModel>> Apply)
{
    public static IReadOnlyList<PersonSort> All { get; } =
    [
        // Most photographs first, by default. The people somebody has the most pictures of are
        // almost always the ones they came to find.
        new("Most photos", people => people.OrderByDescending(p => p.PhotoCount).ThenBy(p => p.Name, NaturalStringComparer.Instance)),
        new("Fewest photos", people => people.OrderBy(p => p.PhotoCount).ThenBy(p => p.Name, NaturalStringComparer.Instance)),
        new("Name A–Z", people => people.OrderBy(p => p.Name, NaturalStringComparer.Instance)),
        new("Name Z–A", people => people.OrderByDescending(p => p.Name, NaturalStringComparer.Instance)),
    ];

    public override string ToString() => Name;
}

/// <summary>Where one face is, so its crop can be fetched when a tile becomes visible.</summary>
/// <remarks>
/// Carried as a value rather than resolved on demand because the screen has already read the face
/// and the photograph to build the tile. Looking them up again when the tile scrolls into view
/// would repeat two queries per person to learn something already known.
/// </remarks>
public sealed record FaceCoverViewModel(FaceId FaceId, string PhotoPath, FaceBox Box);

/// <summary>A named person, their face, and how many photographs they appear in.</summary>
/// <remarks>
/// A class rather than a record because it acquires its picture after it is built, and a record's
/// generated equality would then take the loaded bitmap into account: two tiles for the same
/// person would compare unequal purely because one of them had finished decoding. Identity here is
/// the person's id and nothing else, which is also what a picker needs to keep a selection across
/// a refresh.
/// </remarks>
public sealed partial class PersonTileViewModel(
    PersonId id, string name, int photoCount, ThumbnailLoader thumbnails) : ObservableObject
{
    public PersonId Id { get; } = id;

    public string Name { get; } = name;

    public int PhotoCount { get; } = photoCount;

    /// <summary>Which face to show, or null for a tile that only needs to carry a name.</summary>
    public FaceCoverViewModel? Cover { get; init; }

    public bool HasCover => Cover is not null;

    [ObservableProperty]
    private Bitmap? _coverImage;

    /// <remarks>
    /// Loaded when the tile becomes visible rather than when the list is built, for the same
    /// reason the group tiles are: a library with hundreds of named people would otherwise decode
    /// hundreds of photographs before showing anything at all.
    /// </remarks>
    public async void LoadCoverAsync()
    {
        if (CoverImage is not null || Cover is null)
        {
            return;
        }

        CoverImage = await thumbnails
            .LoadFaceAsync(Cover.FaceId, Cover.PhotoPath, Cover.Box, CancellationToken.None)
            .ConfigureAwait(true);
    }

    public override bool Equals(object? other) => other is PersonTileViewModel person && person.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();

    public string Caption => $"{PhotoCount:N0} photo(s)";

    /// <summary>
    /// First character of the name, shown on the person's badge.
    /// </summary>
    /// <remarks>
    /// Taken by text element rather than by char, so that a name beginning with an emoji or a
    /// character outside the basic plane shows as itself instead of half of a surrogate pair.
    /// </remarks>
    public string Initial
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "?";
            }

            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(Name.Trim());
            return enumerator.MoveNext()
                ? ((string)enumerator.Current).ToUpperInvariant()
                : "?";
        }
    }
}
