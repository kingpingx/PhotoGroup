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
    /// Drops a photograph from the index entirely.
    /// </summary>
    /// <remarks>
    /// For a file that has left the library, not for one the user is finished with. Its faces go
    /// with it, because a face is a region of a photograph that is no longer here; leaving them
    /// would put a person's tile on a picture nothing can open.
    ///
    /// This is the only operation that discards detection work irreversibly, which is why it is
    /// named plainly rather than hidden inside a tidy-up.
    /// </remarks>
    Task RemoveAsync(PhotoId id, CancellationToken ct);

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

    /// <summary>
    /// Records that a detector has examined a photograph, and what it found.
    /// </summary>
    /// <remarks>
    /// Written even when nothing was found. "Examined and found nobody" and "not yet examined"
    /// are different, and without recording the former every photograph containing no people
    /// would be examined again on every run.
    /// </remarks>
    Task RecordDetectionAsync(
        PhotoId id, string detectorId, string detectorVersion, int faceCount, CancellationToken ct);
}
