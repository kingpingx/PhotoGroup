using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>Adds and removes the folders the library indexes.</summary>
public sealed class ManageScanRootsUseCase(IScanRootRepository scanRoots, IFileSystem fileSystem)
{
    public Task<IReadOnlyList<ScanRoot>> ListAsync(CancellationToken ct) => scanRoots.GetAllAsync(ct);

    public async Task<AddScanRootResult> AddAsync(string path, bool recursive, CancellationToken ct)
    {
        if (!fileSystem.DirectoryExists(path))
        {
            return AddScanRootResult.NotFound;
        }

        if (await scanRoots.GetByPathAsync(path, ct).ConfigureAwait(false) is not null)
        {
            return AddScanRootResult.AlreadyPresent;
        }

        await scanRoots.AddAsync(new ScanRoot(ScanRootId.New(), path, recursive), ct).ConfigureAwait(false);
        return AddScanRootResult.Added;
    }

    public Task RemoveAsync(ScanRootId id, CancellationToken ct) => scanRoots.RemoveAsync(id, ct);
}

public enum AddScanRootResult
{
    Added,
    AlreadyPresent,
    NotFound,
}
