using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>Supplies small, upright previews of photos for the grid.</summary>
/// <remarks>
/// Returns a path rather than pixels so the port stays free of any imaging or UI type. The UI
/// loads the file with whatever its toolkit provides, and the cache stays testable without one.
/// </remarks>
public interface IThumbnailCache
{
    /// <summary>Path to the cached thumbnail, generating it if absent. Null if the source cannot be decoded.</summary>
    Task<string?> GetOrCreateAsync(PhotoId id, string sourcePath, CancellationToken ct);

    /// <summary>
    /// Path to a cached crop of one face, generating it if absent. Null if the source cannot be decoded.
    /// </summary>
    /// <remarks>
    /// Kept apart from the photo thumbnail rather than derived from it. A grid thumbnail is a few
    /// hundred pixels on its long edge, so a face occupying a twentieth of the frame survives in it
    /// as a dozen pixels: enough to know somebody is there and nowhere near enough to recognise who.
    /// Cropping from a larger decode is the only way this answers the question it exists to answer.
    ///
    /// Keyed by face rather than by photo, because the whole point is that one photograph yields
    /// several of these and they must not overwrite one another.
    /// </remarks>
    Task<string?> GetOrCreateFaceAsync(FaceId faceId, string sourcePath, FaceBox box, CancellationToken ct);

    /// <summary>Path to an already-cached thumbnail, without generating one.</summary>
    string? TryGetExisting(PhotoId id);

    /// <summary>Discards a photo's thumbnail, after the file changed on disk.</summary>
    void Invalidate(PhotoId id);

    /// <summary>Total bytes currently held, for the settings screen.</summary>
    long GetCacheSizeBytes();

    void Clear();
}
