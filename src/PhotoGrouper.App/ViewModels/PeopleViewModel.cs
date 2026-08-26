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

    /// <summary>Groups that have not been named yet, largest first.</summary>
    public ObservableCollection<ClusterTileViewModel> UnnamedGroups { get; } = [];

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

    public bool HasGroups => UnnamedGroupCount > 0 || NamedPeopleCount > 0;

    /// <summary>How many faces have been dismissed, so they can be brought back.</summary>
    [ObservableProperty]
    private int _ignoredFaceCount;

    public string EmbedderId => ArcFaceEmbedder.Provider.Id;

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
        Detail.Changed = () => RefreshAsync(CancellationToken.None);
        await Detail.OpenAsync(person.Id, person.Name, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSubscribed();

        var records = await clusters.GetAllAsync(DetectorId, EmbedderId, ct).ConfigureAwait(true);
        var named = await people.GetAllAsync(ct).ConfigureAwait(true);

        UnnamedGroups.Clear();
        NamedPeople.Clear();

        foreach (var record in records.Where(r => r.PersonId is null))
        {
            UnnamedGroups.Add(new ClusterTileViewModel(record, thumbnails, faces, photos));
        }

        foreach (var person in named)
        {
            var count = (await faces.GetByPersonAsync(person.Id, DetectorId, ct).ConfigureAwait(true)).Count;
            NamedPeople.Add(new PersonTileViewModel(person.Id, person.Name.Value, count));
        }

        UnnamedGroupCount = UnnamedGroups.Count;
        NamedPeopleCount = NamedPeople.Count;
        IgnoredFaceCount = await ignoreGroup.CountAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasGroups));

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
            ? result.Merged
                ? $"Added {result.FacesAssigned:N0} more photo(s) to {result.Name}."
                : $"{result.Name} now has {result.FacesAssigned:N0} photo(s)."
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

    [ObservableProperty]
    private string _proposedName = string.Empty;

    public ClusterId ClusterId { get; } = record.Id;

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
        if (photo is not null)
        {
            Cover = await thumbnails.LoadAsync(photo.Id, photo.Path, CancellationToken.None).ConfigureAwait(true);
        }
    }
}

/// <summary>A named person and how many photographs they appear in.</summary>
public sealed record PersonTileViewModel(PersonId Id, string Name, int PhotoCount)
{
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
