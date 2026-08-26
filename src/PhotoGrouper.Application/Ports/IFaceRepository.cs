using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>Storage for detected faces.</summary>
/// <remarks>
/// Every read is scoped to a detector. Both detectors' faces live side by side so a switch is
/// reversible, which means a query that forgets to filter returns each person twice and looks
/// like a clustering fault rather than a missing predicate.
/// </remarks>
public interface IFaceRepository
{
    Task BulkInsertAsync(IReadOnlyList<Face> faces, CancellationToken ct);

    Task<IReadOnlyList<Face>> GetByPhotoAsync(PhotoId photoId, string detectorId, CancellationToken ct);

    /// <summary>
    /// Fetches a specific set of faces.
    /// </summary>
    /// <remarks>
    /// Present so that a stage working through faces in batches can ask for the batch it wants.
    /// The alternative, scanning every face and filtering, turns a linear pass over the library
    /// into a quadratic one: at a hundred thousand faces that is billions of rows read to find
    /// tens of thousands.
    /// </remarks>
    Task<IReadOnlyList<Face>> GetByIdsAsync(IReadOnlyList<FaceId> ids, CancellationToken ct);

    Task<IReadOnlyList<Face>> GetByPersonAsync(PersonId personId, string detectorId, CancellationToken ct);

    Task<int> CountAsync(string detectorId, bool activeOnly, CancellationToken ct);

    /// <summary>Removes a photo's faces for one detector, before re-detecting it.</summary>
    Task DeleteByPhotoAsync(PhotoId photoId, string detectorId, CancellationToken ct);

    Task AssignAsync(IReadOnlyList<FaceAssignment> assignments, CancellationToken ct);

    /// <summary>Attaches faces to the groups clustering placed them in.</summary>
    Task SetClustersAsync(IReadOnlyList<FaceClusterAssignment> assignments, CancellationToken ct);

    /// <summary>Faces belonging to one group, for showing and naming it.</summary>
    Task<IReadOnlyList<Face>> GetByClusterAsync(ClusterId clusterId, CancellationToken ct);

    /// <summary>Marks one detector's faces active and every other detector's inactive.</summary>
    Task SetActiveDetectorAsync(string detectorId, CancellationToken ct);

    IAsyncEnumerable<Face> StreamByDetectorAsync(string detectorId, bool activeOnly, CancellationToken ct);
}

/// <param name="PersonId">Null to detach the face from whatever person it was on.</param>
public readonly record struct FaceAssignment(FaceId FaceId, PersonId? PersonId, Assignment Assignment);

/// <param name="ClusterId">Null when the face matched nothing strongly enough to be grouped.</param>
public readonly record struct FaceClusterAssignment(FaceId FaceId, ClusterId? ClusterId);
