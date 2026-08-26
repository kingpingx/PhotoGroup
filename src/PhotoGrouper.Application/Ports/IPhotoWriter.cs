using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Ports;

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
}
