using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>Storage for algorithmic face groups, before they are named.</summary>
/// <remarks>
/// Clusters are derived and disposable: re-running the grouping replaces them entirely. They are
/// stored only because re-deriving them means comparing every face against every other, which
/// takes minutes, and the People page should not pay that to render.
/// </remarks>
public interface IClusterRepository
{
    /// <summary>Replaces every cluster for one detector and embedder pairing.</summary>
    Task ReplaceAllAsync(
        string detectorId, string embedderId, IReadOnlyList<ClusterRecord> clusters, CancellationToken ct);

    Task<IReadOnlyList<ClusterRecord>> GetAllAsync(string detectorId, string embedderId, CancellationToken ct);

    Task<ClusterRecord?> GetByIdAsync(ClusterId id, CancellationToken ct);

    /// <summary>Attaches a cluster to a named person, or detaches it when null.</summary>
    Task SetPersonAsync(ClusterId id, PersonId? personId, CancellationToken ct);

    /// <summary>
    /// Detaches every cluster from a person, so their groups become unnamed again.
    /// </summary>
    /// <remarks>
    /// Used when a person is removed. The groups are kept rather than deleted, so the same faces
    /// can be named correctly straight away without the library being processed again.
    /// </remarks>
    Task ClearPersonAsync(PersonId personId, CancellationToken ct);
}

/// <param name="MedoidFaceId">The most central member, used as the group's cover image.</param>
public readonly record struct ClusterRecord(
    ClusterId Id,
    string DetectorId,
    string EmbedderId,
    int Size,
    FaceId MedoidFaceId,
    DateTimeOffset CreatedUtc,
    PersonId? PersonId = null);
