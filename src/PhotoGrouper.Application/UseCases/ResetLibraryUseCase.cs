using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Returns the application to the state of a fresh installation.
/// </summary>
/// <remarks>
/// Deliberately does not delete the downloaded models. They are large, slow to fetch, and are not
/// part of the library: someone resetting after a bad scan wants their photographs forgotten, not
/// a second wait on a hundred and seventy megabyte download. Removing those is a separate,
/// separately worded action.
///
/// The thumbnails go with the database rather than being left behind. They are keyed by photo id,
/// and after a reset every id is new, so anything kept would be unreachable files occupying disk
/// for nothing.
/// </remarks>
public sealed class ResetLibraryUseCase(IStoreMaintenance store, IThumbnailCache thumbnails)
{
    public Task<StoreContents> DescribeAsync(CancellationToken ct) => store.DescribeAsync(ct);

    /// <summary>True when there is nothing left to clear.</summary>
    public Task<bool> IsEmptyAsync(CancellationToken ct) => store.IsEmptyAsync(ct);

    /// <summary>Bytes held by the thumbnail cache.</summary>
    public long ThumbnailCacheBytes => thumbnails.GetCacheSizeBytes();

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await store.ClearAllAsync(ct).ConfigureAwait(false);
        thumbnails.Clear();
    }
}
