using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoGrouper.App.Services;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.App.ViewModels;

/// <summary>The library screen: scan roots, scan progress, and the photo grid.</summary>
/// <remarks>
/// Depends on use cases rather than on repositories. The distinction is what keeps the
/// rules for scanning in one testable place instead of accumulating in a view model that
/// cannot be exercised without a UI.
/// </remarks>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ScanLibraryUseCase _scanLibrary;
    private readonly ManageScanRootsUseCase _manageScanRoots;
    private readonly IPhotoReader _photos;
    private readonly ThumbnailLoader _thumbnails;

    private CancellationTokenSource? _scanCancellation;

    public LibraryViewModel(
        ScanLibraryUseCase scanLibrary,
        ManageScanRootsUseCase manageScanRoots,
        IPhotoReader photos,
        ThumbnailLoader thumbnails)
    {
        _scanLibrary = scanLibrary;
        _manageScanRoots = manageScanRoots;
        _photos = photos;
        _thumbnails = thumbnails;
    }

    public ObservableCollection<PhotoTileViewModel> Photos { get; } = [];

    public ObservableCollection<ScanRoot> ScanRoots { get; } = [];

    [ObservableProperty]
    private string _status = "No folders added yet.";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private int _photoCount;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await RefreshScanRootsAsync(ct).ConfigureAwait(true);
        await RefreshPhotosAsync(ct).ConfigureAwait(true);
    }

    public async Task AddFolderAsync(string path, CancellationToken ct)
    {
        var result = await _manageScanRoots.AddAsync(path, recursive: true, ct).ConfigureAwait(true);

        Status = result switch
        {
            AddScanRootResult.Added => $"Added {path}. Run a scan to index it.",
            AddScanRootResult.AlreadyPresent => $"{path} is already in the library.",
            AddScanRootResult.NotFound => $"{path} could not be opened.",
            _ => Status,
        };

        await RefreshScanRootsAsync(ct).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;

        IsScanning = true;
        // Total file count is unknown until the walk finishes, so the bar reports motion
        // rather than a fraction. Claiming a percentage here would mean inventing one.
        IsProgressIndeterminate = true;
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();

        var progress = new DelegateProgressSink(update =>
        {
            Status = update.Total is { } total
                ? $"{update.Stage}: {update.Completed:N0} of {total:N0}"
                : $"{update.Stage}: {update.Completed:N0} files";

            if (update.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                ProgressFraction = fraction;
            }
        });

        try
        {
            var result = await Task.Run(
                () => _scanLibrary.ExecuteAsync(progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            Status =
                $"Scan complete. {result.Added:N0} new, {result.Updated:N0} changed, {result.Unchanged:N0} unchanged"
                + (result.SkippedRoots > 0 ? $", {result.SkippedRoots} folder(s) unreachable." : ".");

            await RefreshPhotosAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled. Progress so far has been saved.";
            await RefreshPhotosAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
            ProgressFraction = 0;
            _scanCancellation = null;
            cancellation.Dispose();
            ScanCommand.NotifyCanExecuteChanged();
            CancelScanCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanScan() => !IsScanning && ScanRoots.Count > 0;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private async Task RemoveScanRootAsync(ScanRoot? root)
    {
        if (root is null)
        {
            return;
        }

        await _manageScanRoots.RemoveAsync(root.Id, CancellationToken.None).ConfigureAwait(true);
        Status = $"Removed {root.Path}. Its photos remain indexed.";
        await RefreshScanRootsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshScanRootsAsync(CancellationToken ct)
    {
        var roots = await _manageScanRoots.ListAsync(ct).ConfigureAwait(true);

        ScanRoots.Clear();
        foreach (var root in roots)
        {
            ScanRoots.Add(root);
        }

        ScanCommand.NotifyCanExecuteChanged();
    }

    /// <remarks>
    /// Loads the whole index into tiles. Sound at the scale this milestone targets, but a
    /// fifty thousand photo library will need paging or a virtualising data source here;
    /// the grid virtualises its containers, not the collection behind them.
    /// </remarks>
    private async Task RefreshPhotosAsync(CancellationToken ct)
    {
        Photos.Clear();

        await foreach (var photo in _photos.StreamAllAsync(ct).ConfigureAwait(true))
        {
            Photos.Add(new PhotoTileViewModel(photo, _thumbnails));
        }

        PhotoCount = Photos.Count;

        if (PhotoCount == 0 && ScanRoots.Count > 0)
        {
            Status = "No photos indexed yet. Run a scan.";
        }
    }

    private sealed class DelegateProgressSink(Action<ProgressUpdate> onReport) : IProgressSink
    {
        public void Report(ProgressUpdate update) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onReport(update));
    }
}
