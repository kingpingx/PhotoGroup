using Avalonia.Media.Imaging;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.App.Services;

/// <summary>
/// Supplies grid thumbnails and face crops as bitmaps, keeping a bounded number in memory.
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

    /// <summary>
    /// Face crops held in memory.
    /// </summary>
    /// <remarks>
    /// A smaller allowance than the photographs. A crop is a fraction of the pixels, but it also
    /// appears on far fewer tiles: one per person and one per group, where photographs fill an
    /// entire scrolling grid.
    /// </remarks>
    private const int MaxCachedFaceBitmaps = 200;

    private readonly BitmapStore<PhotoId> _photos = new(MaxCachedBitmaps);
    private readonly BitmapStore<FaceId> _faces = new(MaxCachedFaceBitmaps);

    /// <summary>
    /// Caps concurrent decoding.
    /// </summary>
    /// <remarks>
    /// Fast scrolling can ask for hundreds of thumbnails a second. Without a cap the thread pool
    /// fills with work for tiles that are already off screen, and the UI stops responding to the
    /// very scrolling that queued it.
    ///
    /// Shared between photographs and face crops, because the limit describes the machine rather
    /// than either kind of image; two independent caps would together be twice the intended one.
    /// </remarks>
    private readonly SemaphoreSlim _decodeSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

    public Task<Bitmap?> LoadAsync(PhotoId id, string sourcePath, CancellationToken ct) =>
        _photos.GetOrAddAsync(id, () => cache.GetOrCreateAsync(id, sourcePath, ct), _decodeSlots, ct);

    /// <summary>Loads a crop of one face, for telling apart the people in a photograph.</summary>
    /// <param name="photoPath">The photograph the face was found in.</param>
    /// <param name="box">Where the face sits in it, in full-resolution upright coordinates.</param>
    public Task<Bitmap?> LoadFaceAsync(FaceId id, string photoPath, FaceBox box, CancellationToken ct) =>
        _faces.GetOrAddAsync(id, () => cache.GetOrCreateFaceAsync(id, photoPath, box, ct), _decodeSlots, ct);

    public void Dispose()
    {
        _photos.Dispose();
        _faces.Dispose();
        _decodeSlots.Dispose();
    }

    /// <summary>
    /// A bounded set of decoded bitmaps, keyed by whatever identifies the thing shown.
    /// </summary>
    /// <remarks>
    /// Generic over the key so that photographs and face crops share one implementation of the
    /// awkward part: the eviction order, the lock around it, and the race where two tiles ask for
    /// the same image at once. Written twice, those are two places for a bitmap to be disposed
    /// while a rendered tile still holds it.
    /// </remarks>
    private sealed class BitmapStore<TKey>(int capacity) : IDisposable
        where TKey : notnull
    {
        private readonly Dictionary<TKey, Bitmap> _bitmaps = [];
        private readonly Queue<TKey> _insertionOrder = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<Bitmap?> GetOrAddAsync(
            TKey key, Func<Task<string?>> resolvePath, SemaphoreSlim decodeSlots, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_bitmaps.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }
            finally
            {
                _gate.Release();
            }

            Bitmap? bitmap;
            await decodeSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var path = await resolvePath().ConfigureAwait(false);
                bitmap = path is null ? null : await Task.Run(() => Open(path), ct).ConfigureAwait(false);
            }
            finally
            {
                decodeSlots.Release();
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
                if (_bitmaps.TryGetValue(key, out var raced))
                {
                    bitmap.Dispose();
                    return raced;
                }

                _bitmaps[key] = bitmap;
                _insertionOrder.Enqueue(key);
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
            while (_insertionOrder.Count > capacity)
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
        }
    }
}
