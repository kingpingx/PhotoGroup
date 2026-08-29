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
    /// Photographs whose path contains the given text.
    /// </summary>
    /// <remarks>
    /// For searching by what a camera called a file, which is how somebody looks for a photograph
    /// they remember by date or by burst number rather than by who is in it. Bounded, because the
    /// answer feeds a grid somebody scans and a fragment of one character matches a whole library.
    ///
    /// Matched against the whole path rather than the file name alone, so a folder can be searched
    /// for too; callers that mean the name only narrow it themselves.
    /// </remarks>
    Task<IReadOnlyList<Photo>> SearchByPathAsync(string fragment, int limit, CancellationToken ct);

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
