using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Storage summary and the destructive actions that clear it.
/// </summary>
/// <remarks>
/// Resetting is separated into two actions because the two kinds of data are not alike. The
/// library is irreplaceable: the names attached to people exist only because somebody typed them.
/// The models are merely large, and re-fetching them costs a download rather than anybody's
/// attention. Offering a single "clear everything" would make someone recovering from a bad scan
/// pay for a hundred and seventy megabyte download they never asked to repeat.
/// </remarks>
public sealed partial class SettingsViewModel(
    ResetLibraryUseCase resetLibrary,
    RepairDerivedDataUseCase repairDerivedData,
    ModelStore models,
    LibraryChangedNotifier libraryChanged) : ObservableObject
{
    [ObservableProperty]
    private string _librarySummary = "Loading...";

    [ObservableProperty]
    private string _modelSummary = "Loading...";

    /// <summary>What the last repair found, or nothing before one has been run.</summary>
    [ObservableProperty]
    private string _repairStatus = string.Empty;

    public bool HasRepairStatus => !string.IsNullOrEmpty(RepairStatus);

    partial void OnRepairStatusChanged(string value) => OnPropertyChanged(nameof(HasRepairStatus));

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// True once the user has asked to reset and is being asked to confirm.
    /// </summary>
    /// <remarks>
    /// A second press rather than a dialog. The action destroys work that cannot be recovered, and
    /// a button that does that on a single click sits one misplaced press away from a bad day.
    /// </remarks>
    [ObservableProperty]
    private bool _isConfirmingReset;

    [ObservableProperty]
    private bool _isConfirmingModelDelete;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// False once there is nothing left to clear.
    /// </summary>
    /// <remarks>
    /// An enabled button that does nothing visible is worse than a disabled one: it leaves someone
    /// pressing it repeatedly, wondering whether the reset is working.
    /// </remarks>
    [ObservableProperty]
    private bool _canClearLibrary;

    [ObservableProperty]
    private bool _hasModels;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var contents = await resetLibrary.DescribeAsync(ct).ConfigureAwait(true);
        var thumbnails = resetLibrary.ThumbnailCacheBytes;

        CanClearLibrary = !await resetLibrary.IsEmptyAsync(ct).ConfigureAwait(true);

        // An empty library still occupies some space, because the tables themselves remain so the
        // application keeps working without a restart. Saying so is clearer than reporting a size
        // that looks like leftover data somebody failed to remove.
        var storage = CanClearLibrary
            ? $"Database {Format(contents.SizeOnDiskBytes)}, thumbnails {Format(thumbnails)}."
            : $"Empty. {Format(contents.SizeOnDiskBytes)} of empty tables remain so the app keeps working.";

        LibrarySummary = CanClearLibrary
            ? $"{contents.Photos:N0} photo(s), {contents.Faces:N0} face(s), "
              + $"{contents.Embeddings:N0} recognised, {contents.People:N0} named person/people, "
              + $"{contents.Clusters:N0} group(s), {contents.ScanRoots:N0} folder(s).\n"
              + storage
            : $"Nothing stored.\n{storage}";

        ModelSummary = DescribeModels();

        // A confirmation left showing for a library that is now empty would offer to destroy
        // nothing.
        IsConfirmingReset = IsConfirmingReset && CanClearLibrary;
    }

    private string DescribeModels()
    {
        var present = new List<string>();
        long bytes = 0;

        foreach (var model in new[] { YuNetDetector.Model, ScrfdDetector.Model, ArcFaceEmbedder.Model })
        {
            var path = models.PathFor(model);
            if (File.Exists(path))
            {
                present.Add(model.DisplayName);
                bytes += new FileInfo(path).Length;
            }
        }

        HasModels = present.Count > 0;

        return present.Count == 0
            ? "No models downloaded yet. They are fetched the first time they are needed."
            : $"{string.Join(", ", present)}. {Format(bytes)} on disk.";
    }

    /// <summary>
    /// Rebuilds the values nobody typed, where they have drifted from the faces.
    /// </summary>
    /// <remarks>
    /// Here rather than on the People screen because it is maintenance on data the user never sees
    /// directly, and Settings is already where operations on the store as a whole live. No
    /// confirmation, unlike everything else on this screen: it recomputes from the faces rather
    /// than adjusting what is there, so pressing it twice is indistinguishable from pressing it
    /// once and nothing it does can be regretted.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task RepairAsync()
    {
        IsBusy = true;
        RepairStatus = "Checking every person and group...";

        try
        {
            var result = await Task.Run(
                () => repairDerivedData.ExecuteAsync(
                    DetectorRegistry.DefaultDetectorId,
                    ArcFaceEmbedder.Provider.Id,
                    new DelegateProgressSink(update => RepairStatus = Describe(update)),
                    CancellationToken.None))
                .ConfigureAwait(true);

            RepairStatus = result.FoundNothingWrong
                ? $"Checked {result.PeopleCalibrated:N0} person/people. Nothing needed correcting."
                : $"Checked {result.PeopleCalibrated:N0} person/people. Corrected "
                  + $"{result.AveragesCleared:N0} stale average(s), {result.CoversRepaired:N0} "
                  + $"cover picture(s) and {result.GroupsResized:N0} group size(s)"
                  + (result.EmptyGroupsRemoved > 0
                      ? $", and removed {result.EmptyGroupsRemoved:N0} empty group(s)."
                      : ".");

            if (!result.FoundNothingWrong)
            {
                libraryChanged.NotifyChanged();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(ProgressUpdate update) =>
        update.Total is { } total
            ? $"{update.Stage}: {update.Completed:N0} of {total:N0}"
            : $"{update.Stage}: {update.Completed:N0}";

    private sealed class DelegateProgressSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(ProgressUpdate update) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onReport(update));
    }

    [RelayCommand(CanExecute = nameof(CanBeginReset))]
    private void BeginReset() => IsConfirmingReset = true;

    private bool CanBeginReset() => !IsBusy && CanClearLibrary;

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    /// <summary>Clears the library. Downloaded models are kept.</summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ConfirmResetAsync()
    {
        IsBusy = true;
        IsConfirmingReset = false;
        Status = "Clearing the library...";

        try
        {
            await resetLibrary.ExecuteAsync(CancellationToken.None).ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);

            // The other screens are holding folders, tiles and groups that no longer exist. Without
            // this they keep showing them until something else happens to reload them.
            libraryChanged.NotifyChanged();

            Status = "Library cleared. Add a folder and scan to start again. "
                     + "Downloaded models were kept, so there is nothing to re-download.";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Status = $"Could not clear the library: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBeginModelDelete))]
    private void BeginModelDelete() => IsConfirmingModelDelete = true;

    private bool CanBeginModelDelete() => !IsBusy && HasModels;

    [RelayCommand]
    private void CancelModelDelete() => IsConfirmingModelDelete = false;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ConfirmModelDeleteAsync()
    {
        IsBusy = true;
        IsConfirmingModelDelete = false;

        try
        {
            var removed = 0;

            foreach (var model in new[] { YuNetDetector.Model, ScrfdDetector.Model, ArcFaceEmbedder.Model })
            {
                var path = models.PathFor(model);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            Status = removed == 0
                ? "There were no downloaded models to remove."
                : $"Removed {removed} model file(s). They will be downloaded again when next needed.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not remove the models: {e.Message}. They may be in use; try again after grouping finishes.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsNotBusy() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => RefreshCommandStates();

    partial void OnCanClearLibraryChanged(bool value) => RefreshCommandStates();

    partial void OnHasModelsChanged(bool value) => RefreshCommandStates();

    private void RefreshCommandStates()
    {
        BeginResetCommand.NotifyCanExecuteChanged();
        BeginModelDeleteCommand.NotifyCanExecuteChanged();
        ConfirmResetCommand.NotifyCanExecuteChanged();
        ConfirmModelDeleteCommand.NotifyCanExecuteChanged();
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };
}
