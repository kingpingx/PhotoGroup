using PhotoGrouper.Application.People;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Folds several names for one person into a single name.
/// </summary>
/// <remarks>
/// The correction for a person who was named twice. It exists as its own operation rather than
/// being done with the pieces already here, because doing it with those pieces gets it subtly
/// wrong: moving the faces and then removing the emptied person detaches that person's groups, and
/// a detached group with no faces of its own comes straight back to the People screen asking to be
/// named. The user would merge two people and immediately be offered a third.
///
/// The groups are therefore re-pointed at the surviving person rather than released. A group is a
/// statement about which faces belong together, and merging two names does not make that statement
/// wrong.
/// </remarks>
public sealed class MergePeopleUseCase(
    IPersonRepository people,
    IFaceRepository faces,
    IClusterRepository clusters,
    PersonCalibrator calibrator)
{
    /// <summary>Moves everything from the other people onto <paramref name="keepId"/>.</summary>
    public async Task<MergeResult> ExecuteAsync(
        PersonId keepId,
        IReadOnlyList<PersonId> mergeIds,
        string detectorId,
        string embedderId,
        CancellationToken ct)
    {
        var keep = await people.GetByIdAsync(keepId, ct).ConfigureAwait(false);
        if (keep is null)
        {
            return MergeResult.Failed("The person to keep no longer exists.");
        }

        var sources = mergeIds.Where(id => id != keepId).Distinct().ToList();
        if (sources.Count == 0)
        {
            return MergeResult.Failed("Choose at least one other name to merge in.");
        }

        // Read once. Re-reading per source would be a query for every group in the library for
        // every name being merged, and the set does not change while this runs.
        var allClusters = await clusters
            .GetAllAsync(detectorId, embedderId, ct)
            .ConfigureAwait(false);

        var mergedPeople = 0;
        var movedFaces = 0;

        foreach (var sourceId in sources)
        {
            ct.ThrowIfCancellationRequested();

            var source = await people.GetByIdAsync(sourceId, ct).ConfigureAwait(false);
            if (source is null)
            {
                continue;
            }

            var assigned = await faces.GetByPersonAsync(sourceId, detectorId, ct).ConfigureAwait(false);

            // Marked as decided by hand, because it was. Somebody has looked at two faces and said
            // they are one person, and the next automatic run must not quietly undo that.
            //
            // A face the user had rejected keeps its rejection: they said that face is not this
            // person, and merging two names is not a statement about that face.
            await faces.AssignAsync(
                [.. assigned
                    .Where(face => face.Assignment != Assignment.Rejected)
                    .Select(face => new FaceAssignment(face.Id, keepId, Assignment.Confirmed))],
                ct).ConfigureAwait(false);

            movedFaces += assigned.Count(face => face.Assignment != Assignment.Rejected);

            // Re-pointed, not released. This is the step that separates a merge from a deletion,
            // and leaving it out is what would put the merged person's group back on the screen as
            // something new to name.
            foreach (var cluster in allClusters.Where(c => c.PersonId == sourceId))
            {
                await clusters.SetPersonAsync(cluster.Id, keepId, ct).ConfigureAwait(false);
            }

            await people.RemoveAsync(sourceId, ct).ConfigureAwait(false);
            mergedPeople++;
        }

        // Recomputed last, once, over everything the surviving person now holds. Updating it per
        // source would leave the average describing a subset at every step but the final one, and
        // it is what the next grouping run compares new faces against.
        await calibrator.CalibrateAsync(keepId, detectorId, embedderId, ct).ConfigureAwait(false);

        return MergeResult.Succeeded(keep.Name.Value, mergedPeople, movedFaces);
    }

}

/// <param name="MergedPeople">How many names were folded away.</param>
/// <param name="MovedFaces">Faces that changed hands.</param>
public readonly record struct MergeResult(
    bool IsSuccess, string? Name, int MergedPeople, int MovedFaces, string? Error)
{
    public static MergeResult Succeeded(string name, int people, int faces) =>
        new(true, name, people, faces, null);

    public static MergeResult Failed(string error) => new(false, null, 0, 0, error);
}
