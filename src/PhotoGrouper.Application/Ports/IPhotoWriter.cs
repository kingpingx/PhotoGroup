using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Ports;

/// <param name="Width">Width of the upright image, after EXIF orientation is applied.</param>
/// <param name="Height">Height of the upright image, after EXIF orientation is applied.</param>
public readonly record struct ImageDetails(
    int Width, int Height, int Orientation, DateTimeOffset? TakenUtc, string? Camera);

/// <summary>Write access to the photo index, used by the scan and export pipelines.</summary>
public interface IPhotoWriter
{
    Task<PhotoId> UpsertAsync(Photo photo, CancellationToken ct);

    /// <summary>
    /// Inserts or updates a batch in a single unit.
    /// </summary>
    /// <remarks>
    /// Part of the port rather than a convenience wrapper: a scan writes tens of thousands
    /// of rows, and row-at-a-time insertion is the difference between a three minute scan
    /// and a forty minute one. Keeping it in the contract means no adapter can be written
    /// that only supports the slow path.
    /// </remarks>
    Task BulkUpsertAsync(IReadOnlyList<Photo> photos, CancellationToken ct);

    Task SetStateAsync(PhotoId id, PhotoState state, string? error, CancellationToken ct);

    /// <summary>Rewrites the recorded path after a move export relocated the file.</summary>
    Task UpdatePathAsync(PhotoId id, string newPath, CancellationToken ct);

    /// <summary>
    /// Records what decoding revealed about the image.
    /// </summary>
    /// <remarks>
    /// Written by the detection stage, which has already paid to decode the file. Without these
    /// the library knows a photo's size on disk but not its dimensions, so nothing can relate a
    /// stored face box to the image it describes without decoding the original again.
    ///
    /// Dimensions are of the upright image, after orientation has been applied, because that is
    /// the space face coordinates live in.
    /// </remarks>
    Task UpdateImageDetailsAsync(PhotoId id, ImageDetails details, CancellationToken ct);
}
