using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoGrouper.App.Services;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.App.ViewModels;

/// <summary>One photo in the library grid.</summary>
/// <remarks>
/// Thumbnails load only once a tile is actually realised by the virtualising panel, and the
/// load is cancelled if the tile is recycled before it finishes. Without that cancellation,
/// scrolling quickly through a large library leaves a backlog of decodes for tiles that are
/// long since off screen, and the thumbnails the user is looking at queue behind them.
/// </remarks>
public sealed partial class PhotoTileViewModel(Photo photo, ThumbnailLoader thumbnails) : ObservableObject
{
    private CancellationTokenSource? _loadCancellation;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isLoading;

    public Photo Photo { get; } = photo;

    public string FileName { get; } = System.IO.Path.GetFileName(photo.Path);

    public string Path => Photo.Path;

    public string Tooltip { get; } = BuildTooltip(photo);

    public async void OnAttached()
    {
        if (Thumbnail is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _loadCancellation, cancellation)?.Cancel();

        try
        {
            IsLoading = true;
            Thumbnail = await thumbnails.LoadAsync(Photo.Id, Photo.Path, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected: the tile scrolled out of view before its thumbnail was ready.
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OnDetached()
    {
        Interlocked.Exchange(ref _loadCancellation, null)?.Cancel();
        IsLoading = false;
    }

    private static string BuildTooltip(Photo photo)
    {
        var size = photo.FileSize switch
        {
            >= 1024 * 1024 => $"{photo.FileSize / 1024.0 / 1024.0:F1} MB",
            >= 1024 => $"{photo.FileSize / 1024.0:F0} KB",
            _ => $"{photo.FileSize} B",
        };

        var taken = photo.TakenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "date unknown";
        return $"{photo.Path}\n{size} · {taken}";
    }
}
