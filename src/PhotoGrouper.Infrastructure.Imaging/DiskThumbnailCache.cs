using OpenCvSharp;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>
/// Keeps small upright JPEG previews on disk, one per photo.
/// </summary>
/// <remarks>
/// Thumbnails live on disk rather than in the database. A library of fifty thousand photos
/// produces several hundred megabytes of them, and putting that inside SQLite would slow every
/// backup, vacuum and integrity check of a file whose other contents are small and precious.
/// On disk they are also trivially disposable: the cache can be deleted at any time and simply
/// rebuilds.
///
/// Files are spread across subdirectories by the first byte of the photo id. Fifty thousand
/// entries in one folder makes ordinary operations, including the app's own enumeration and any
/// attempt to inspect the folder by hand, unpleasantly slow on NTFS.
/// </remarks>
public sealed class DiskThumbnailCache(string rootDirectory, IImageDecoder decoder) : IThumbnailCache
{
    /// <summary>
    /// Long edge of a generated thumbnail.
    /// </summary>
    /// <remarks>
    /// Larger than the 180 pixel grid tile so that the image still looks sharp on a high-DPI
    /// display, where a tile occupies substantially more physical pixels than logical ones.
    /// </remarks>
    public const int ThumbnailLongEdge = 320;

    private const int JpegQuality = 82;

    public async Task<string?> GetOrCreateAsync(PhotoId id, string sourcePath, CancellationToken ct)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            return path;
        }

        var decoded = await decoder.DecodeAsync(sourcePath, ThumbnailLongEdge, ct).ConfigureAwait(false);
        if (decoded is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var mat = MatBridge.ToMat(decoded.Buffer);

            // Encoded to memory rather than written straight to the temporary path, because
            // OpenCV chooses its encoder from the file extension and a name ending in .tmp
            // matches none of them: it writes nothing and reports no error.
            if (!Cv2.ImEncode(".jpg", mat, out var encoded, [(int)ImwriteFlags.JpegQuality, JpegQuality]))
            {
                return null;
            }

            // Written under a temporary name and then moved into place. A half-written JPEG left
            // behind by a crash would otherwise be indistinguishable from a good one, and would
            // keep failing to load for as long as the cache survived.
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encoded, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OpenCVException)
        {
            return null;
        }
    }

    public string? TryGetExisting(PhotoId id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? path : null;
    }

    public void Invalidate(PhotoId id)
    {
        try
        {
            File.Delete(PathFor(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A thumbnail that cannot be deleted is stale, not fatal. It will be replaced the
            // next time the photo is processed.
        }
    }

    public long GetCacheSizeBytes()
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(rootDirectory)
                .EnumerateFiles("*.jpg", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Clear()
    {
        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing here is irreplaceable; a locked file just stays until next time.
        }
    }

    private string PathFor(PhotoId id)
    {
        var hex = Convert.ToHexString(Uuid7.ToBigEndian(id.Value));
        return Path.Combine(rootDirectory, hex[..2], hex + ".jpg");
    }
}
