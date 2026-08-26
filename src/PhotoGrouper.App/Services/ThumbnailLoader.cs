using Avalonia.Media.Imaging;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.App.Services;

/// <summary>
/// Decodes photo thumbnails on demand, holding a bounded number in memory.
/// </summary>
/// <remarks>
/// A deliberate placeholder for the real thumbnail cache. It decodes straight from the
/// original file through Avalonia's own decoder, which is enough to put photos on screen
/// but does two things the shipped version must not: it re-decodes a full-size image every
/// time a tile scrolls back into view, and it ignores the EXIF orientation tag, so photos
/// shot in portrait on a phone appear sideways. Both are addressed when the imaging
/// adapter arrives with a disk-backed cache of pre-rotated thumbnails.
///
/// The bound matters more than the caching. A library of fifty thousand photos would
/// exhaust memory in seconds if every tile that had ever been shown kept its bitmap alive,
/// so entries are evicted in insertion order once the cap is reached.
/// </remarks>
public sealed class ThumbnailLoader : IDisposable
{
    private const int MaxCachedBitmaps = 400;
    private const int DecodeWidth = 256;

    private readonly Dictionary<PhotoId, Bitmap> _cache = [];
    private readonly Queue<PhotoId> _insertionOrder = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Limits how many files are decoded at once.
    /// </summary>
    /// <remarks>
    /// Fast scrolling can request hundreds of thumbnails within a second. Without a cap the
    /// thread pool fills with decode work and the UI stops responding to the very scrolling
    /// that queued it.
    /// </remarks>
    private readonly SemaphoreSlim _decodeSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

    public async Task<Bitmap?> LoadAsync(PhotoId id, string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(id, out var cached))
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
            bitmap = await Task.Run(() => Decode(path), ct).ConfigureAwait(false);
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
            // A concurrent request may have finished first. Keep the existing instance so
            // that bitmaps already bound to a rendered tile are never disposed underneath it.
            if (_cache.TryGetValue(id, out var raced))
            {
                bitmap.Dispose();
                return raced;
            }

            _cache[id] = bitmap;
            _insertionOrder.Enqueue(id);
            Evict();
            return bitmap;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DecodeWidth);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A file that will not decode is shown as an empty tile rather than taking the
            // grid down. The scan pipeline records the failure properly against the photo.
            return null;
        }
    }

    private void Evict()
    {
        while (_insertionOrder.Count > MaxCachedBitmaps)
        {
            var oldest = _insertionOrder.Dequeue();
            if (_cache.Remove(oldest, out var bitmap))
            {
                bitmap.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var bitmap in _cache.Values)
        {
            bitmap.Dispose();
        }

        _cache.Clear();
        _gate.Dispose();
        _decodeSlots.Dispose();
    }
}
