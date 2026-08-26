using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Storage for the review decisions that clustering must obey.
/// </summary>
/// <remarks>
/// Small, and the most valuable table in the database. Everything else can be rebuilt from the
/// photographs; these exist only because somebody sat and answered questions, and losing them
/// means asking again.
/// </remarks>
public interface IFaceLinkRepository
{
    Task<IReadOnlyList<FaceLink>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Records a decision about a pair.
    /// </summary>
    /// <remarks>
    /// Implementations must normalise the pair so that recording the same two faces in either
    /// order yields one entry rather than two contradictory ones.
    /// </remarks>
    Task AddAsync(FaceId a, FaceId b, FaceLinkKind kind, CancellationToken ct);

    Task RemoveAsync(FaceId a, FaceId b, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}

public readonly record struct FaceLink(FaceId FaceA, FaceId FaceB, FaceLinkKind Kind);

public enum FaceLinkKind
{
    /// <summary>The user confirmed these are the same person.</summary>
    Must = 0,

    /// <summary>The user confirmed these are different people.</summary>
    Cannot = 1,
}
