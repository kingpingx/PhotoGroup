using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Dismisses a group of faces the user does not want to name.
/// </summary>
/// <remarks>
/// Most faces in a photo library belong to nobody in particular: people in the background, on
/// posters, walking past. Without a way to say so, those groups sit on the People screen forever
/// asking for a name, and the only way to clear them is to invent one.
///
/// The faces are recorded as dismissed rather than deleted. Deleting them would discard detections
/// that cost real time and would simply be found again by the next detection run, whereas a
/// dismissal is a statement about interest that survives.
/// </remarks>
public sealed class IgnoreGroupUseCase(IFaceRepository faces, IIgnoredFaceRepository ignored)
{
    public async Task<int> ExecuteAsync(ClusterId clusterId, CancellationToken ct)
    {
        var members = await faces.GetByClusterAsync(clusterId, ct).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return 0;
        }

        await ignored.AddAsync([.. members.Select(face => face.Id)], ct).ConfigureAwait(false);

        // Detached from the group as well, so the People screen stops showing them the moment the
        // action is taken rather than only after the next grouping run.
        await faces.SetClustersAsync(
            [.. members.Select(face => new FaceClusterAssignment(face.Id, null))],
            ct).ConfigureAwait(false);

        return members.Count;
    }

    /// <summary>Brings every dismissed face back into consideration.</summary>
    public Task RestoreAllAsync(CancellationToken ct) => ignored.RemoveAllAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => ignored.CountAsync(ct);
}
