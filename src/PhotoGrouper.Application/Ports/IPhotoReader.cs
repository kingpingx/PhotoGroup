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

    Task<int> CountAsync(CancellationToken ct);

    /// <summary>Streams every photo in id order, without materialising the whole library.</summary>
    IAsyncEnumerable<Photo> StreamAllAsync(CancellationToken ct);
}
