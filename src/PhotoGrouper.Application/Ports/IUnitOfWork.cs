namespace PhotoGrouper.Application.Ports;

/// <summary>Groups several writes into one atomic unit.</summary>
/// <remarks>
/// Load-bearing, not decorative. A move export must rewrite the photo's path and mark the
/// export operation done together; if only one of those survives a crash, the library
/// points at a file that is no longer there.
///
/// Worth knowing before choosing a backend: a standalone MongoDB server does not support
/// multi-document transactions at all. They require a replica set, even a single-node one.
/// Any document-store adapter would have to be deployed that way to satisfy this port.
/// </remarks>
public interface IUnitOfWork
{
    Task<ITransactionScope> BeginAsync(CancellationToken ct);
}

/// <summary>An open transaction. Disposing without committing rolls back.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
