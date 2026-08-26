namespace PhotoGrouper.Application.Ports;

/// <summary>Operations on the store as a whole rather than on any one kind of record.</summary>
public interface IStoreMaintenance
{
    /// <summary>
    /// Removes every record, returning the store to the state of a fresh installation.
    /// </summary>
    /// <remarks>
    /// Irreversible, and destroys the one thing in the application that cannot be recomputed:
    /// the names a user has given to people. Callers are expected to confirm first.
    /// </remarks>
    Task ClearAllAsync(CancellationToken ct);

    /// <summary>A count of what the store currently holds, for display before a reset.</summary>
    Task<StoreContents> DescribeAsync(CancellationToken ct);

    /// <summary>
    /// True when there is nothing left to clear.
    /// </summary>
    /// <remarks>
    /// Offering a reset for a library that is already empty invites someone to press it and wonder
    /// whether it worked, since nothing visible changes.
    /// </remarks>
    Task<bool> IsEmptyAsync(CancellationToken ct);
}

/// <param name="ScanRoots">Folders the library indexes.</param>
/// <param name="SizeOnDiskBytes">
/// Size of the database and its write-ahead log, excluding thumbnails and models. The log is
/// included because it routinely holds more than the database during and after heavy writing, and
/// reporting only the database would understate what the library occupies.
/// </param>
public readonly record struct StoreContents(
    int Photos,
    int Faces,
    int Embeddings,
    int People,
    int Clusters,
    int ScanRoots,
    long SizeOnDiskBytes);
