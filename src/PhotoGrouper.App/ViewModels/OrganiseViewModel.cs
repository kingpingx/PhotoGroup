using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Writes the library out as folders, one per person.
/// </summary>
/// <remarks>
/// The point at which all of this leaves the application and becomes something any other program
/// can read: a folder called Alice holding every photograph of Alice. Until now the grouping has
/// only existed inside this app.
///
/// Copy is the default and move is the one that needs the warning, because a move takes somebody's
/// originals out of the folders they chose for them. It is offered at all only because every move
/// is journalled and can be put back.
/// </remarks>
public sealed partial class OrganiseViewModel(
    ExportPhotosUseCase export,
    UndoExportUseCase undo,
    IPersonRepository people,
    IExportRepository exports,
    LibraryChangedNotifier libraryChanged) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private bool _subscribed;

    public ObservableCollection<OrganisePersonViewModel> People { get; } = [];

    public ObservableCollection<ExportRunViewModel> Runs { get; } = [];

    [ObservableProperty]
    private string _outputRoot = string.Empty;

    public bool HasOutputRoot => !string.IsNullOrWhiteSpace(OutputRoot);

    partial void OnOutputRootChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputRoot));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// True to take the originals out of their folders rather than copying them.
    /// </summary>
    /// <remarks>
    /// Defaults to false, and stays the harder of the two to choose. A copy costs disk space; a
    /// move rearranges photographs somebody has filed themselves, on the strength of grouping this
    /// application did automatically.
    /// </remarks>
    [ObservableProperty]
    private bool _moveInsteadOfCopy;

    [ObservableProperty]
    private bool _everybody = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _status = string.Empty;

    public string DetectorId { get; set; } = DetectorRegistry.DefaultDetectorId;

    public int ChosenCount => People.Count(person => person.IsChosen);

    public string ActionCaption => MoveInsteadOfCopy
        ? "Move photos into folders"
        : "Copy photos into folders";

    partial void OnMoveInsteadOfCopyChanged(bool value) => OnPropertyChanged(nameof(ActionCaption));

    partial void OnEverybodyChanged(bool value) => ExportCommand.NotifyCanExecuteChanged();

    public async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSubscribed();

        var chosen = People.Where(p => p.IsChosen).Select(p => p.Id).ToHashSet();

        People.Clear();
        foreach (var person in (await people.GetAllAsync(ct).ConfigureAwait(true))
                     .OrderBy(person => person.Name.Value, NaturalStringComparer.Instance))
        {
            People.Add(new OrganisePersonViewModel(person.Id, person.Name.Value, OnChosenChanged)
            {
                IsChosen = chosen.Contains(person.Id),
            });
        }

        Runs.Clear();
        foreach (var run in await exports.GetRecentRunsAsync(8, ct).ConfigureAwait(true))
        {
            Runs.Add(new ExportRunViewModel(run));
        }

        OnPropertyChanged(nameof(ChosenCount));
        ExportCommand.NotifyCanExecuteChanged();
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
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        IsProgressIndeterminate = true;

        var progress = new DelegateProgressSink(update =>
        {
            Status = update.Total is { } total
                ? $"{update.Stage}: {update.Completed:N0} of {total:N0}"
                : $"{update.Stage}...";

            if (update.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                ProgressFraction = fraction;
            }
        });

        try
        {
            var request = new ExportRequest(
                OutputRoot,
                MoveInsteadOfCopy ? ExportMode.Move : ExportMode.Copy,
                Everybody ? ExportSource.EveryNamedPerson : ExportSource.ChosenPeople,
                [.. People.Where(p => p.IsChosen).Select(p => p.Id)],
                DetectorId);

            var result = await Task.Run(
                () => export.ExecuteAsync(request, progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            Status = result.IsSuccess
                ? Describe(result)
                : result.Error!;

            if (result.IsSuccess)
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);

                // A move changed where the library's files are, so every screen holding a path is
                // now describing somewhere that no longer holds anything.
                if (result.Mode == ExportMode.Move && result.Written > 0)
                {
                    libraryChanged.NotifyChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Everything written so far stays where it was put.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressFraction = 0;
            _cancellation = null;
            cancellation.Dispose();
        }
    }

    private static string Describe(ExportResult result) =>
        $"{(result.Mode == ExportMode.Move ? "Moved" : "Copied")} {result.Written:N0} photo(s) "
        + $"into {result.Folder}"
        + (result.Skipped > 0 ? $", {result.Skipped:N0} already there" : string.Empty)
        + (result.FailedCount > 0 ? $", {result.FailedCount:N0} could not be written" : string.Empty)
        + (result.Cancelled ? ", stopped early" : string.Empty)
        + ".";

    private bool CanExport() => !IsBusy && HasOutputRoot && (Everybody || ChosenCount > 0);

    partial void OnIsBusyChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Puts back the files a move took away.</summary>
    [RelayCommand]
    private async Task UndoAsync(ExportRunViewModel? run)
    {
        if (run is null || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await undo
                .ExecuteAsync(run.Id, new DelegateProgressSink(_ => { }), CancellationToken.None)
                .ConfigureAwait(true);

            Status = result.IsSuccess
                ? $"Put back {result.Restored:N0} photo(s)."
                  + (result.Blocked > 0
                      ? $" {result.Blocked:N0} could not be returned; try again once whatever is in "
                        + "the way has been moved."
                      : string.Empty)
                : result.Error!;

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            libraryChanged.NotifyChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed class DelegateProgressSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(ProgressUpdate update) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onReport(update));
    }
}

/// <summary>One named person, and whether this export covers them.</summary>
public sealed partial class OrganisePersonViewModel(
    PersonId id, string name, Action onChosenChanged) : ObservableObject
{
    public PersonId Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isChosen;

    partial void OnIsChosenChanged(bool value) => onChosenChanged();
}

/// <summary>One past export, and whether it can still be put back.</summary>
public sealed class ExportRunViewModel(ExportRun run)
{
    public ExportRunId Id { get; } = run.Id;

    public string Caption { get; } =
        $"{(run.Mode == ExportMode.Move ? "Moved" : "Copied")} to {run.OutputRoot}";

    public string When { get; } = run.StartedUtc.LocalDateTime.ToString("d MMM yyyy, HH:mm");

    public string Outcome { get; } = run.Status switch
    {
        ExportRunStatus.Completed => "finished",
        ExportRunStatus.Failed => "finished with problems",
        ExportRunStatus.Cancelled => "stopped early",
        ExportRunStatus.Undone => "put back",
        _ => "running",
    };

    /// <summary>Only a move can be undone, and only once.</summary>
    public bool CanBeUndone { get; } = run.CanBeUndone;
}
