using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Read access to the photo index.
/// </summary>
/// <remarks>
/// Split from <see cref="IPhotoWriter"/> so that presentation code can be given the
/// ability to read the library without the ability to modify it. A view model that
/// cannot write photos is a view model that cannot corrupt the library.
///
/// Deliberately exposes no IQueryable and no connection. Intent-revealing methods let a
/// SQLite adapter use a prepared statement and a document-store adapter use its own bulk
/// API, without either shape dictating the other; an IQueryable would silently re-couple
/// this layer to one provider's expression translation.
/// </remarks>
public interface IPhotoReader
{
    Task<Photo?> GetByIdAsync(PhotoId id, CancellationToken ct);

    Task<Photo?> GetByPathAsync(string path, CancellationToken ct);

    Task<IReadOnlyList<Photo>> GetByStateAsync(PhotoState state, int limit, CancellationToken ct);

    /// <summary>
    /// Photographs the given detector has not examined yet.
    /// </summary>
    /// <remarks>
    /// Detection progress is per-detector, not per-photo. Asking instead for photographs in a
    /// "not yet detected" state would mean that examining a library with one detector marked it
    /// finished for every detector, and switching to the other would silently find no work.
    ///
    /// A photograph whose file has changed since it was examined is included again, because its
    /// stored faces describe pixels that no longer exist.
    /// </remarks>
    Task<IReadOnlyList<Photo>> GetPhotosNeedingDetectionAsync(string detectorId, int limit, CancellationToken ct);

    /// <summary>How many photographs still await examination by this detector.</summary>
    Task<int> CountPhotosNeedingDetectionAsync(string detectorId, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);

    /// <summary>Streams every photo in id order, without materialising the whole library.</summary>
    IAsyncEnumerable<Photo> StreamAllAsync(CancellationToken ct);
}
