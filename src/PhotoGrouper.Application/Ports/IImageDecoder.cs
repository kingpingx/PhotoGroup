using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Application.Ports;

/// <summary>Turns an image file on disk into pixels the rest of the app can work with.</summary>
/// <remarks>
/// A port so that adding a format is adding an adapter. RAW is the obvious next one, and it
/// should not require touching the pipeline that consumes the result.
/// </remarks>
public interface IImageDecoder
{
    /// <summary>True when this decoder handles the given file's extension.</summary>
    bool CanDecode(string path);

    /// <summary>
    /// Decodes a file, applying EXIF orientation and optionally limiting the long edge.
    /// </summary>
    /// <param name="maxLongEdge">
    /// When set, the image is scaled down so its longer side does not exceed this. Detection
    /// gains nothing from full resolution and costs a great deal, so callers pass a bound.
    /// </param>
    /// <returns>The decoded image, or null when the file cannot be read.</returns>
    Task<DecodedImage?> DecodeAsync(string path, int? maxLongEdge, CancellationToken ct);

    /// <summary>Reads metadata without decoding pixels.</summary>
    Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken ct);
}

/// <summary>Pixels plus what is needed to relate them back to the original file.</summary>
/// <param name="Buffer">The pixels, already rotated to their upright orientation.</param>
/// <param name="OriginalWidth">Width of the upright full-resolution image, before any downscale.</param>
/// <param name="OriginalHeight">Height of the upright full-resolution image, before any downscale.</param>
/// <param name="Scale">
/// Factor applied to reach <paramref name="Buffer"/> from the full-resolution image. Detection
/// runs on the downscaled pixels, so coordinates must be divided by this to be stored against
/// the original.
/// </param>
public sealed record DecodedImage(ImageBuffer Buffer, int OriginalWidth, int OriginalHeight, float Scale);

/// <param name="Orientation">EXIF orientation tag, 1 to 8. Already applied to any decoded pixels.</param>
public sealed record ImageMetadata(
    int Width,
    int Height,
    int Orientation,
    DateTimeOffset? TakenUtc,
    string? Camera);
