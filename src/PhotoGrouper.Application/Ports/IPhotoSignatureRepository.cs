using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Ports;

/// <summary>Storage for what each photograph looks like, used to find near-duplicates.</summary>
/// <remarks>
/// Separate from the photo index because it is derived and recomputed in batches, and because a
/// photo row is read on every screen while this is read by one. The same reasoning that keeps face
/// embeddings out of the faces table.
/// </remarks>
public interface IPhotoSignatureRepository
{
    Task BulkUpsertAsync(IReadOnlyList<PhotoSignature> signatures, CancellationToken ct);

    /// <summary>
    /// Photographs that have no fingerprint yet.
    /// </summary>
    /// <remarks>
    /// The basis of resuming. Fingerprinting a large library takes minutes, and a user who closes
    /// the application part way through must not have to start again.
    /// </remarks>
    Task<IReadOnlyList<Photo>> GetPhotosNeedingSignatureAsync(int limit, CancellationToken ct);

    Task<int> CountPhotosNeedingSignatureAsync(CancellationToken ct);

    /// <summary>Every fingerprint, for the comparison that finds duplicates.</summary>
    /// <remarks>
    /// Returned whole rather than streamed. Comparison is between every pair, so there is no
    /// partial view of this that answers anything; and at sixteen bytes each, a library of a
    /// million photographs is a few tens of megabytes.
    /// </remarks>
    Task<IReadOnlyList<PhotoSignature>> GetAllAsync(CancellationToken ct);
}

/// <param name="Hash">What the photograph looks like, compared by how many bits differ.</param>
/// <param name="Sharpness">How much fine detail it carries, for choosing between near-identical frames.</param>
public readonly record struct PhotoSignature(PhotoId PhotoId, PerceptualHash Hash, double Sharpness);
