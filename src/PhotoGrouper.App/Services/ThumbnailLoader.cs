using Avalonia.Media.Imaging;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.App.Services;

/// <summary>
/// Supplies grid thumbnails as bitmaps, keeping a bounded number in memory.
/// </summary>
/// <remarks>
/// A thin layer over the thumbnail cache. The cache does the real work: it stores small,
/// already-rotated JPEGs on disk, so nothing here re-decodes a twelve megapixel photo every time
/// a tile scrolls back into view, and nothing displays a portrait phone photo on its side.
///
/// The memory bound matters as much as the caching. Fifty thousand tiles that each kept their
/// bitmap alive would exhaust memory in seconds, so entries are evicted in insertion order.
/// </remarks>
public sealed class ThumbnailLoader(IThumbnailCache cache) : IDisposable
{
    private const int MaxCachedBitmaps = 400;

    private readonly Dictionary<PhotoId, Bitmap> _bitmaps = [];
    private readonly Queue<PhotoId> _insertionOrder = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Caps concurrent decoding.
    /// </summary>
    /// <remarks>
    /// Fast scrolling can ask for hundreds of thumbnails a second. Without a cap the thread pool
    /// fills with work for tiles that are already off screen, and the UI stops responding to the
    /// very scrolling that queued it.
    /// </remarks>
    private readonly SemaphoreSlim _decodeSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

    public async Task<Bitmap?> LoadAsync(PhotoId id, string sourcePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bitmaps.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }
        finally
        {
            _gate.Release();
        }

        Bitmap? bitmap;
        await _decodeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = await cache.GetOrCreateAsync(id, sourcePath, ct).ConfigureAwait(false);
            bitmap = path is null ? null : await Task.Run(() => Open(path), ct).ConfigureAwait(false);
        }
        finally
        {
            _decodeSlots.Release();
        }

        if (bitmap is null)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent request may have won. Keep the existing instance so a bitmap already
            // bound to a rendered tile is never disposed out from under it.
            if (_bitmaps.TryGetValue(id, out var raced))
            {
                bitmap.Dispose();
                return raced;
            }

            _bitmaps[id] = bitmap;
            _insertionOrder.Enqueue(id);
            Evict();
            return bitmap;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Bitmap? Open(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A thumbnail that will not open shows as an empty tile rather than taking the grid
            // down. The scan pipeline records the underlying failure against the photo.
            return null;
        }
    }

    private void Evict()
    {
        while (_insertionOrder.Count > MaxCachedBitmaps)
        {
            var oldest = _insertionOrder.Dequeue();
            if (_bitmaps.Remove(oldest, out var bitmap))
            {
                bitmap.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
        _gate.Dispose();
        _decodeSlots.Dispose();
    }
}
