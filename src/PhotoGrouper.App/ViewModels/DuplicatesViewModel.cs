using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Finding the same picture more than once, and moving the extras out of the way.
/// </summary>
/// <remarks>
/// The one screen in this application that touches the user's own files, so it is built to be
/// argued with. Nothing is moved without a folder chosen for the purpose and a button pressed;
/// every set shows all of its members at once rather than a verdict; and the suggestion of which
/// one to keep is a preselection to be overridden, not a decision already taken.
/// </remarks>
public sealed partial class DuplicatesViewModel(
    IndexPhotoSignaturesUseCase indexSignatures,
    FindDuplicatePhotosUseCase findDuplicates,
    QuarantineDuplicatesUseCase quarantine,
    ThumbnailLoader thumbnails,
    LibraryChangedNotifier libraryChanged) : ObservableObject
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _status = "Nothing looked at yet.";

    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>
    /// Where the extras go.
    /// </summary>
    /// <remarks>
    /// Deliberately has no default. A folder chosen for somebody is a folder they have not looked
    /// at, and the whole reason this moves files rather than deleting them is so that they can.
    /// </remarks>
    [ObservableProperty]
    private string _quarantineFolder = string.Empty;

    public bool HasQuarantineFolder => !string.IsNullOrWhiteSpace(QuarantineFolder);

    partial void OnQuarantineFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuarantineFolder));
        MoveSelectedCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedBytes;

    public string SelectionCaption => SelectedCount == 0
        ? "Nothing selected"
        : $"Move {SelectedCount:N0} photo(s), {Megabytes(SelectedBytes)}";

    public string EmptyCaption => HasSearched
        ? "No repeated pictures found."
        : "Press Find duplicates to compare every photo in the library.";

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        Status = HasSearched ? Status : "Nothing looked at yet.";
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _cancellation?.Cancel();
    }

    /// <summary>
    /// Fingerprints anything new, then compares everything.
    /// </summary>
    /// <remarks>
    /// The two run together because separately they are meaningless to a user: a fingerprint on its
    /// own shows nothing, and a comparison without one finds nothing. They stay separate use cases
    /// so the expensive half resumes on its own.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task FindAsync()
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        IsProgressIndeterminate = true;
        FindCommand.NotifyCanExecuteChanged();

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
            var indexed = await Task.Run(
                () => indexSignatures.ExecuteAsync(progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            IsProgressIndeterminate = true;
            Status = "Comparing photos...";

            var found = await Task.Run(
                () => findDuplicates.ExecuteAsync(
                    FindDuplicatePhotosUseCase.DefaultMaximumDistance,
                    FindDuplicatePhotosUseCase.DefaultMaximumApart,
                    cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            Groups.Clear();
            foreach (var group in found)
            {
                Groups.Add(new DuplicateGroupViewModel(group, thumbnails, OnSelectionChanged));
            }

            HasSearched = true;
            OnPropertyChanged(nameof(EmptyCaption));
            OnSelectionChanged();

            Status = found.Count == 0
                ? "No repeated pictures found."
                : $"{found.Count:N0} set(s) of repeats, {Megabytes(found.Sum(g => g.RecoverableBytes))} "
                  + "if every extra is moved."
                  + (indexed.Failed > 0 ? $" {indexed.Failed:N0} file(s) could not be read." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Photos already read will not be read again.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressFraction = 0;
            _cancellation = null;
            cancellation.Dispose();
            FindCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsNotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Selects every photo except the one suggested for keeping, in every set.</summary>
    [RelayCommand]
    private void SelectAllExtras()
    {
        foreach (var group in Groups)
        {
            foreach (var member in group.Members)
            {
                member.SetSelected(!member.IsKeeper);
            }
        }

        OnSelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var group in Groups)
        {
            foreach (var member in group.Members)
            {
                member.SetSelected(false);
            }
        }

        OnSelectionChanged();
    }

    /// <summary>
    /// Moves the selected photos into the chosen folder.
    /// </summary>
    /// <remarks>
    /// Refuses to empty a set. Selecting every member of a group is how somebody loses a picture
    /// entirely rather than losing its copies, and the check costs nothing beside the cost of being
    /// wrong.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveSelectedAsync()
    {
        var emptied = Groups.Where(g => g.Members.All(m => m.IsSelected)).ToList();
        if (emptied.Count > 0)
        {
            Status = $"{emptied.Count:N0} set(s) have every photo selected. "
                     + "Leave one in each, or those pictures disappear from the library entirely.";
            return;
        }

        var chosen = Groups
            .SelectMany(g => g.Members)
            .Where(m => m.IsSelected)
            .Select(m => m.PhotoId)
            .ToList();

        IsBusy = true;
        MoveSelectedCommand.NotifyCanExecuteChanged();

        try
        {
            var result = await quarantine
                .ExecuteAsync(chosen, QuarantineFolder, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                Status = result.Error!;
                return;
            }

            Status = $"Moved {result.Moved:N0} photo(s) to {result.Folder}, "
                     + $"freeing {Megabytes(result.BytesRecovered)}."
                     + (result.AlreadyGone > 0
                         ? $" {result.AlreadyGone:N0} had already been moved and were dropped from the library."
                         : string.Empty);

            RemoveMoved(chosen);

            // The photographs are gone from the index, so the library grid and the people screen
            // are both describing a state that no longer exists until they rebuild.
            libraryChanged.NotifyChanged();
        }
        finally
        {
            IsBusy = false;
            MoveSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanMove() => !IsBusy && SelectedCount > 0 && HasQuarantineFolder;

    /// <summary>
    /// Takes the moved photos off the screen without searching again.
    /// </summary>
    /// <remarks>
    /// A set reduced to one photograph is no longer a set of repeats, so it goes too. Re-running
    /// the whole comparison would give the same answer at the cost of reading every file again.
    /// </remarks>
    private void RemoveMoved(IReadOnlyList<PhotoId> moved)
    {
        var gone = moved.ToHashSet();

        foreach (var group in Groups.ToList())
        {
            foreach (var member in group.Members.Where(m => gone.Contains(m.PhotoId)).ToList())
            {
                group.Members.Remove(member);
            }

            if (group.Members.Count < 2)
            {
                Groups.Remove(group);
            }
            else
            {
                group.Refresh();
            }
        }

        OnSelectionChanged();
        OnPropertyChanged(nameof(EmptyCaption));
    }

    private void OnSelectionChanged()
    {
        var selected = Groups.SelectMany(g => g.Members).Where(m => m.IsSelected).ToList();

        SelectedCount = selected.Count;
        SelectedBytes = selected.Sum(m => m.SizeBytes);
        OnPropertyChanged(nameof(SelectionCaption));
        MoveSelectedCommand.NotifyCanExecuteChanged();
    }

    internal static string Megabytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:N2} GB",
    };

    private sealed class DelegateProgressSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(ProgressUpdate update) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onReport(update));
    }
}

/// <summary>One set of photographs that are the same picture.</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(
        DuplicateGroup group, ThumbnailLoader thumbnails, Action onSelectionChanged)
    {
        Members = [.. group.Members.Select((member, index) =>
            new DuplicatePhotoViewModel(member, isKeeper: index == 0, thumbnails, onSelectionChanged))];

        Refresh();
    }

    public ObservableCollection<DuplicatePhotoViewModel> Members { get; }

    [ObservableProperty]
    private string _caption = string.Empty;

    /// <remarks>
    /// Recomputed after a move rather than left describing the set as it was found. A caption that
    /// still says eight copies over five tiles is the kind of small dishonesty that makes somebody
    /// stop trusting the numbers on the rest of the screen.
    /// </remarks>
    public void Refresh()
    {
        var extras = Members.Count - 1;
        var bytes = Members.Skip(1).Sum(m => m.SizeBytes);

        Caption = $"{Members.Count:N0} copies of one picture — "
                  + $"{extras:N0} extra, {DuplicatesViewModel.Megabytes(bytes)}";
    }
}

/// <summary>One photograph within a set, and whether it has been chosen to move.</summary>
public sealed partial class DuplicatePhotoViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnails;
    private readonly Action _onSelectionChanged;
    private readonly string _path;

    public DuplicatePhotoViewModel(
        DuplicateMember member, bool isKeeper, ThumbnailLoader thumbnails, Action onSelectionChanged)
    {
        _thumbnails = thumbnails;
        _onSelectionChanged = onSelectionChanged;
        _path = member.Photo.Path;

        PhotoId = member.Photo.Id;
        IsKeeper = isKeeper;
        SizeBytes = member.Photo.FileSize;
        FileName = System.IO.Path.GetFileName(member.Photo.Path);
        Folder = System.IO.Path.GetDirectoryName(member.Photo.Path) ?? string.Empty;

        Dimensions = member.Photo is { Width: { } width, Height: { } height }
            ? $"{width:N0} × {height:N0}"
            : "size unknown";

        Size = DuplicatesViewModel.Megabytes(member.Photo.FileSize);

        Taken = (member.Photo.TakenUtc ?? member.Photo.ModifiedUtc).LocalDateTime
            .ToString("d MMM yyyy, HH:mm:ss");

        // Shown as a plain word rather than the number. The variance of a Laplacian is meaningless
        // to a reader, and its only use here is to say which of two frames of one scene is the
        // crisper, so that is what it says.
        Detail = isKeeper
            ? "sharpest of the set"
            : member.DistanceFromBest == 0
                ? "identical to the keeper"
                : $"{member.DistanceFromBest} of 64 points differ";

        // Everything but the suggested keeper starts selected, because that is what somebody asking
        // to remove duplicates means. It is a preselection on a screen where nothing happens until
        // a folder is chosen and a button pressed, not a decision made for them.
        //
        // Set through the property rather than the field so the count on the toolbar is right the
        // moment the sets appear, instead of after the first click.
        IsSelected = !isKeeper;
    }

    public PhotoId PhotoId { get; }

    public bool IsKeeper { get; }

    public long SizeBytes { get; }

    public string FileName { get; }

    public string Folder { get; }

    public string Dimensions { get; }

    public string Size { get; }

    public string Taken { get; }

    public string Detail { get; }

    public string FullPath => _path;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    private bool _isBulkChanging;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_isBulkChanging)
        {
            _onSelectionChanged();
        }
    }

    /// <summary>
    /// Sets the flag without announcing it, for the commands that set hundreds at once.
    /// </summary>
    /// <remarks>
    /// Each announcement re-totals every photograph in every set, so letting the bulk commands
    /// announce per item makes selecting all of them cost the square of their number. The caller
    /// announces once when it has finished.
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

    public void Toggle() => IsSelected = !IsSelected;

    public async void LoadThumbnailAsync()
    {
        if (Thumbnail is null)
        {
            Thumbnail = await _thumbnails
                .LoadAsync(PhotoId, _path, CancellationToken.None)
                .ConfigureAwait(true);
        }
    }
}
