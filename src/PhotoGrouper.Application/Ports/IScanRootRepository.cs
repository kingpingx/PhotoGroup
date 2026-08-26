using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Ports;

/// <summary>Storage for the folders the library indexes.</summary>
public interface IScanRootRepository
{
    Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken ct);

    Task<ScanRoot?> GetByPathAsync(string path, CancellationToken ct);

    Task AddAsync(ScanRoot root, CancellationToken ct);

    Task RemoveAsync(ScanRootId id, CancellationToken ct);

    Task MarkScannedAsync(ScanRootId id, DateTimeOffset whenUtc, CancellationToken ct);
}
