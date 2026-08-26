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

    /// <summary>Path to an already-cached thumbnail, without generating one.</summary>
    string? TryGetExisting(PhotoId id);

    /// <summary>Discards a photo's thumbnail, after the file changed on disk.</summary>
    void Invalidate(PhotoId id);

    /// <summary>Total bytes currently held, for the settings screen.</summary>
    long GetCacheSizeBytes();

    void Clear();
}
