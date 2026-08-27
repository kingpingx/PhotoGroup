using OpenCvSharp;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
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

    /// <summary>
    /// Long edge a face crop is decoded from.
    /// </summary>
    /// <remarks>
    /// A compromise, and the only number here worth arguing about. Decoding at full resolution
    /// would make even a distant face sharp and would cost a full decode of a twelve megapixel
    /// photograph per face; decoding at the thumbnail's own 320 would cost nothing and produce a
    /// smear. At this size a face filling a tenth of the frame still arrives with enough pixels to
    /// be recognised, which is the job.
    /// </remarks>
    public const int FaceSourceLongEdge = 1600;

    /// <summary>Long edge of a stored face crop.</summary>
    public const int FaceCropLongEdge = 256;

    /// <summary>
    /// How far the stored box is grown before cropping, as a proportion of its size.
    /// </summary>
    /// <remarks>
    /// A detector's box stops at the chin and the hairline. That is what an embedder wants and it
    /// is a poor portrait: people recognise each other by hair, ears and jaw as much as by the
    /// features inside the box. The margin is clamped to the image, so a face at the edge of the
    /// frame simply gets less of it rather than a crop that falls outside the picture.
    /// </remarks>
    private const float FaceMargin = 0.35f;

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
            using var mat = MatBridge.ToMat(decoded.Buffer);
            return await WriteJpegAsync(path, mat, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OpenCVException)
        {
            return null;
        }
    }

    /// <summary>Encodes a Mat and puts it at the given path, atomically.</summary>
    private static async Task<string?> WriteJpegAsync(string path, Mat image, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Encoded to memory rather than written straight to the temporary path, because
        // OpenCV chooses its encoder from the file extension and a name ending in .tmp
        // matches none of them: it writes nothing and reports no error.
        if (!Cv2.ImEncode(".jpg", image, out var encoded, [(int)ImwriteFlags.JpegQuality, JpegQuality]))
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

    /// <remarks>
    /// The stored box is in full-resolution upright coordinates and the decode is bounded, so the
    /// box is scaled by the decoder's own reported factor before being used. Reproducing that
    /// arithmetic from the image dimensions instead would be one more place for a scale factor to
    /// be got wrong, and a wrong one here crops confidently to the wrong part of the photograph.
    /// </remarks>
    public async Task<string?> GetOrCreateFaceAsync(
        FaceId faceId, string sourcePath, FaceBox box, CancellationToken ct)
    {
        var path = FacePathFor(faceId);
        if (File.Exists(path))
        {
            return path;
        }

        var decoded = await decoder.DecodeAsync(sourcePath, FaceSourceLongEdge, ct).ConfigureAwait(false);
        if (decoded is null)
        {
            return null;
        }

        try
        {
            using var mat = MatBridge.ToMat(decoded.Buffer);

            var region = ScaleToDecoded(
                box.Expand(FaceMargin, decoded.OriginalWidth, decoded.OriginalHeight),
                decoded.Scale,
                mat.Width,
                mat.Height);

            // A box that lands outside the decoded pixels means the stored coordinates and this
            // photograph no longer describe each other: the file has been replaced, or rotated,
            // since detection ran. Nothing sensible can be cropped, and cropping anyway would put a
            // confident picture of the wrong thing on somebody's tile.
            if (region.Width <= 0 || region.Height <= 0)
            {
                return null;
            }

            using var crop = new Mat(mat, region);
            using var scaled = Downscale(crop);

            return await WriteJpegAsync(path, scaled, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OpenCVException)
        {
            return null;
        }
    }

    /// <summary>Brings a full-resolution box into the decoded image's coordinates, clamped to it.</summary>
    private static Rect ScaleToDecoded(FaceBox box, float scale, int width, int height)
    {
        var left = Math.Clamp((int)MathF.Floor(box.X * scale), 0, width);
        var top = Math.Clamp((int)MathF.Floor(box.Y * scale), 0, height);
        var right = Math.Clamp((int)MathF.Ceiling(box.Right * scale), 0, width);
        var bottom = Math.Clamp((int)MathF.Ceiling(box.Bottom * scale), 0, height);

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Shrinks a crop to the stored size, leaving a smaller one alone.</summary>
    /// <remarks>
    /// Deliberately never enlarges. A distant face is worth what it is worth, and storing an
    /// upscaled copy would cost disk space to make the same information look more authoritative
    /// than it is.
    /// </remarks>
    private static Mat Downscale(Mat crop)
    {
        var longEdge = Math.Max(crop.Width, crop.Height);
        if (longEdge <= FaceCropLongEdge)
        {
            return crop.Clone();
        }

        var factor = (double)FaceCropLongEdge / longEdge;
        var target = new Size(
            Math.Max(1, (int)Math.Round(crop.Width * factor)),
            Math.Max(1, (int)Math.Round(crop.Height * factor)));

        var resized = new Mat();
        Cv2.Resize(crop, resized, target, interpolation: InterpolationFlags.Area);
        return resized;
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

    private string PathFor(PhotoId id) => Spread(rootDirectory, id.Value);

    /// <remarks>
    /// Under the same root as the photo thumbnails, in a subdirectory of its own. That keeps
    /// clearing the cache and reporting its size covering both without either having to be taught
    /// that face crops exist, and it keeps a face id and a photo id from ever colliding on a name.
    /// </remarks>
    private string FacePathFor(FaceId id) => Spread(Path.Combine(rootDirectory, "faces"), id.Value);

    /// <summary>
    /// Spreads files across subdirectories by the first byte of the id.
    /// </summary>
    /// <remarks>
    /// Fifty thousand entries in one folder makes ordinary operations, including the app's own
    /// enumeration and any attempt to inspect the folder by hand, unpleasantly slow on NTFS.
    /// </remarks>
    private static string Spread(string root, Guid id)
    {
        var hex = Convert.ToHexString(Uuid7.ToBigEndian(id));
        return Path.Combine(root, hex[..2], hex + ".jpg");
    }
}
