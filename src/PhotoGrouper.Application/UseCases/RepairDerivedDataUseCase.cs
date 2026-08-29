using PhotoGrouper.Application.People;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Rebuilds the values nobody typed, for a library where they have drifted.
/// </summary>
/// <remarks>
/// Everything this touches is derived and none of it announces that it has gone stale. Until
/// recently a person's average was not recomputed when photographs were taken off them, and it is
/// still not recomputed when a photograph is deleted and its faces cascade away — which is exactly
/// what removing duplicate photographs does. A group's recorded size has never been corrected after
/// its first grouping run, and that number decides the order groups appear in, whether a group is
/// large enough to be worth naming at all, and the totals in the header.
///
/// So this exists for libraries that predate the fixes, and for the paths that still leave a mess.
/// It is idempotent by construction — it recomputes from the faces rather than adjusting what is
/// there — which is what makes it safe to offer as a button somebody can press twice.
///
/// One asymmetry worth knowing: a person has a single slot for an average, and averages from two
/// embedders cannot be compared at all. People are therefore calibrated for the active pairing only,
/// while groups can be repaired for whichever pairing they record, because both ids are on the row.
/// </remarks>
public sealed class RepairDerivedDataUseCase(
    IPersonRepository people,
    IFaceRepository faces,
    IClusterRepository clusters,
    IEmbeddingRepository embeddings,
    PersonCalibrator calibrator)
{
    public async Task<RepairResult> ExecuteAsync(
        string detectorId, string embedderId, IProgressSink progress, CancellationToken ct)
    {
        var everybody = await people.GetAllAsync(ct).ConfigureAwait(false);

        progress.Report(new ProgressUpdate("Recalculating people", 0, everybody.Count));

        var calibrated = 0;
        var coversRepaired = 0;
        var lostAverages = 0;

        foreach (var person in everybody)
        {
            ct.ThrowIfCancellationRequested();

            var before = person.Centroid is { Length: > 0 };
            var result = await calibrator
                .CalibrateAsync(person, detectorId, embedderId, ct)
                .ConfigureAwait(false);

            calibrated++;

            if (result.CoverChanged)
            {
                coversRepaired++;
            }

            // Worth reporting separately: a person who had an average and now has none was
            // describing faces they no longer hold, which is the failure this repairs.
            if (before && !result.HasCentroid)
            {
                lostAverages++;
            }

            progress.Report(new ProgressUpdate("Recalculating people", calibrated, everybody.Count));
        }

        var (resized, removed) = await RepairClustersAsync(detectorId, embedderId, progress, ct)
            .ConfigureAwait(false);

        return new RepairResult(calibrated, lostAverages, coversRepaired, resized, removed);
    }

    /// <summary>
    /// Corrects every group's recorded size, and its cover where that face has gone.
    /// </summary>
    /// <remarks>
    /// Empty groups are removed first, so the pass that follows does not have to invent a central
    /// face for a group that has none — the schema stores that id as nullable but the record does
    /// not, and writing null there fails on the next read rather than at the point of the mistake.
    /// </remarks>
    private async Task<(int Resized, int Removed)> RepairClustersAsync(
        string detectorId, string embedderId, IProgressSink progress, CancellationToken ct)
    {
        var removed = await clusters.RemoveEmptyAsync(detectorId, embedderId, ct).ConfigureAwait(false);

        var all = await clusters.GetAllAsync(detectorId, embedderId, ct).ConfigureAwait(false);
        progress.Report(new ProgressUpdate("Recalculating groups", 0, all.Count));

        var resized = 0;
        var examined = 0;

        foreach (var cluster in all)
        {
            ct.ThrowIfCancellationRequested();
            examined++;

            var members = await faces.GetByClusterAsync(cluster.Id, ct).ConfigureAwait(false);
            if (members.Count == 0)
            {
                continue;
            }

            var medoid = members.Any(face => face.Id == cluster.MedoidFaceId)
                ? cluster.MedoidFaceId
                : await ChooseMedoidAsync(members, embedderId, ct).ConfigureAwait(false);

            if (members.Count != cluster.Size || medoid != cluster.MedoidFaceId)
            {
                await clusters.SetSizeAsync(cluster.Id, members.Count, medoid, ct).ConfigureAwait(false);
                resized++;
            }

            progress.Report(new ProgressUpdate("Recalculating groups", examined, all.Count));
        }

        return (resized, removed);
    }

    /// <summary>
    /// The most central surviving member of a group.
    /// </summary>
    /// <remarks>
    /// Computed only when the recorded one has gone, which is the minority. The most central face
    /// is the one most like all the others, which is what makes it a fair picture of the group; with
    /// no vectors to compare, the largest face, which is the rule used everywhere a cover has to be
    /// guessed.
    /// </remarks>
    private async Task<FaceId> ChooseMedoidAsync(
        IReadOnlyList<Domain.Faces.Face> members, string embedderId, CancellationToken ct)
    {
        var vectors = await embeddings
            .GetManyAsync([.. members.Select(face => face.Id)], embedderId, ct)
            .ConfigureAwait(false);

        if (vectors.Count == 0)
        {
            return members
                .OrderByDescending(face => face.Box.SmallestSide)
                .ThenByDescending(face => face.Box.Score)
                .First().Id;
        }

        var best = vectors[0].FaceId;
        var bestScore = float.NegativeInfinity;

        foreach (var candidate in vectors)
        {
            var score = 0f;
            foreach (var other in vectors)
            {
                if (other.FaceId != candidate.FaceId)
                {
                    score += Dot(candidate.Vector, other.Vector);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.FaceId;
            }
        }

        return best;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}

/// <param name="PeopleCalibrated">Everybody whose average and cover were rebuilt.</param>
/// <param name="AveragesCleared">People who were describing faces they no longer hold.</param>
/// <param name="CoversRepaired">Tiles that were pointing at a face the person does not have.</param>
/// <param name="GroupsResized">Groups whose recorded size or cover no longer matched their members.</param>
/// <param name="EmptyGroupsRemoved">Groups that had lost every face they held.</param>
public readonly record struct RepairResult(
    int PeopleCalibrated,
    int AveragesCleared,
    int CoversRepaired,
    int GroupsResized,
    int EmptyGroupsRemoved)
{
    /// <summary>True when the library was already correct.</summary>
    public bool FoundNothingWrong =>
        AveragesCleared == 0 && CoversRepaired == 0 && GroupsResized == 0 && EmptyGroupsRemoved == 0;
}
