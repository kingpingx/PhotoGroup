using PhotoGrouper.Application.Clustering;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Groups every embedded face into candidate people.
/// </summary>
/// <remarks>
/// The step that turns a pile of vectors into something a person can act on. Its output is not
/// yet people: it is groups of faces that appear to be the same person, waiting for a name.
/// The distinction matters because a group is derived and can be recomputed at will, whereas a
/// name is the one thing in this application that cannot.
/// </remarks>
public sealed class ClusterFacesUseCase(
    IEmbeddingRepository embeddings,
    IFaceRepository faces,
    IClusterRepository clusters,
    IFaceLinkRepository links,
    IPersonRepository people,
    IIgnoredFaceRepository ignored,
    IVectorIndex index,
    IClock clock)
{
    /// <summary>
    /// How close a new group must be to an existing person to be recognised as them.
    /// </summary>
    /// <remarks>
    /// Set above the threshold used to link individual faces. A group's centroid is an average of
    /// several photographs and is therefore a cleaner signal than any single face, so demanding
    /// more of it costs little; and the consequence of being wrong here is worse. Attaching a group
    /// to the wrong person silently files a stranger's photographs under somebody's name, whereas
    /// failing to attach merely leaves a group waiting to be named.
    /// </remarks>
    public const float PersonMatchThreshold = 0.5f;
    /// <summary>
    /// Neighbours considered per face.
    /// </summary>
    /// <remarks>
    /// Large enough that a person photographed many times stays connected through the group, small
    /// enough that a face in a crowded photograph does not drag in half the library. Twenty is
    /// comfortably above the number of photographs most people appear in together.
    /// </remarks>
    private const int NeighboursPerFace = 20;

    /// <summary>
    /// Similarity below which two faces are not considered related at all.
    /// </summary>
    /// <remarks>
    /// Measured on this project's reference photographs, the same person scored between 0.62 and
    /// 0.81 and different people between -0.06 and 0.18. The gap is wide, and 0.35 sits in the
    /// middle of it rather than at either edge, so neither a slightly unusual photograph of one
    /// person nor an unusually similar pair of strangers lands on the wrong side by a hair.
    ///
    /// It is a starting point, not a constant of nature. Faces of children, and of the same person
    /// many years apart, sit lower than these numbers suggest.
    /// </remarks>
    public const float DefaultSimilarityThreshold = 0.35f;

    /// <summary>
    /// Smallest group that becomes a candidate person.
    /// </summary>
    /// <remarks>
    /// A single face matching nothing else is usually a stranger in the background or a poor
    /// detection, not someone the user wants to name. Those go to the unsorted pile rather than
    /// filling the People page with hundreds of entries of one photograph each.
    /// </remarks>
    public const int MinimumClusterSize = 2;

    public async Task<ClusteringResult> ExecuteAsync(
        string detectorId,
        string embedderId,
        float similarityThreshold,
        IProgressSink progress,
        CancellationToken ct)
    {
        progress.Report(new ProgressUpdate("Loading faces", 0, null));

        // Dismissed faces are left out before anything else happens, rather than being filtered
        // from the result. A stranger who still takes part in the comparison can pull genuine faces
        // into their group and change who ends up with whom.
        var dismissed = await ignored.GetAllAsync(ct).ConfigureAwait(false);

        var vectors = new List<FaceEmbedding>();
        await foreach (var embedding in embeddings
                           .StreamByEmbedderAsync(embedderId, detectorId, ct)
                           .ConfigureAwait(false))
        {
            if (!dismissed.Contains(embedding.FaceId))
            {
                vectors.Add(embedding);
            }
        }

        if (vectors.Count == 0)
        {
            return new ClusteringResult(0, 0, 0);
        }

        var positions = new Dictionary<FaceId, int>(vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            positions[vectors[i].FaceId] = i;
        }

        var constraints = await LoadConstraintsAsync(positions, ct).ConfigureAwait(false);

        var neighbours = await index
            .FindNeighboursAsync(vectors, NeighboursPerFace, similarityThreshold, progress, ct)
            .ConfigureAwait(false);

        progress.Report(new ProgressUpdate("Grouping faces", 0, null));
        var labels = ChineseWhispers.Cluster(neighbours, constraints);

        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < labels.Length; i++)
        {
            if (!members.TryGetValue(labels[i], out var list))
            {
                list = [];
                members[labels[i]] = list;
            }

            list.Add(i);
        }

        var assignments = new List<FaceClusterAssignment>(vectors.Count);
        var records = new List<ClusterRecord>();
        var memberships = new Dictionary<ClusterId, List<int>>();
        var singletons = 0;

        foreach (var (_, group) in members)
        {
            if (group.Count < MinimumClusterSize)
            {
                singletons += group.Count;

                // Left without a cluster rather than given one of their own. The People page reads
                // clusters, and a hundred one-photograph groups would bury the people who matter.
                foreach (var position in group)
                {
                    assignments.Add(new FaceClusterAssignment(vectors[position].FaceId, null));
                }

                continue;
            }

            var clusterId = ClusterId.New();
            var medoid = FindMedoid(vectors, group);

            records.Add(new ClusterRecord(
                clusterId, detectorId, embedderId, group.Count, vectors[medoid].FaceId, clock.UtcNow));
            memberships[clusterId] = group;

            foreach (var position in group)
            {
                assignments.Add(new FaceClusterAssignment(vectors[position].FaceId, clusterId));
            }
        }

        // Groups are replaced wholesale, because cluster identity carries no meaning between runs
        // and keeping the previous set would leave faces pointing at groups that no longer describe
        // them. That alone used to lose every name: the new groups arrived unattached, so people
        // who had been named appeared unnamed, and naming one of those groups again created a
        // second person who took the faces, leaving the original with none. Recognising the people
        // again, below, is what makes re-grouping safe to press twice.
        await clusters.ReplaceAllAsync(detectorId, embedderId, records, ct).ConfigureAwait(false);
        await faces.SetClustersAsync(assignments, ct).ConfigureAwait(false);

        var recognised = await RecogniseKnownPeopleAsync(records, vectors, memberships, ct)
            .ConfigureAwait(false);

        progress.Report(new ProgressUpdate("Grouping faces", vectors.Count, vectors.Count));
        return new ClusteringResult(records.Count, vectors.Count - singletons, singletons, recognised);
    }

    /// <summary>
    /// Re-attaches new groups to people who have already been named.
    /// </summary>
    /// <remarks>
    /// This is what makes grouping repeatable. Each named person carries the average of their face
    /// vectors, so a freshly formed group can be compared against everyone already known and
    /// recognised as one of them.
    ///
    /// A group's centroid is compared rather than its individual faces: averaging several
    /// photographs cancels out the lighting and angle of any one of them, which is exactly the
    /// noise that makes single-face matching unreliable.
    ///
    /// Faces the user has decided about by hand are never touched. Somebody who removed a
    /// photograph from a person said that face is not them, and no amount of similarity is allowed
    /// to overrule it.
    /// </remarks>
    private async Task<int> RecogniseKnownPeopleAsync(
        IReadOnlyList<ClusterRecord> records,
        IReadOnlyList<FaceEmbedding> vectors,
        IReadOnlyDictionary<ClusterId, List<int>> memberships,
        CancellationToken ct)
    {
        var known = (await people.GetAllAsync(ct).ConfigureAwait(false))
            .Where(person => person.Centroid is { Length: > 0 })
            .ToList();

        if (known.Count == 0)
        {
            return 0;
        }

        var recognised = 0;

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();

            if (!memberships.TryGetValue(record.Id, out var group))
            {
                continue;
            }

            var centroid = Centroid(vectors, group);
            if (centroid is null)
            {
                continue;
            }

            var best = known
                .Select(person => (Person: person, Similarity: Dot(centroid, person.Centroid!)))
                .OrderByDescending(match => match.Similarity)
                .First();

            if (best.Similarity < PersonMatchThreshold)
            {
                continue;
            }

            await clusters.SetPersonAsync(record.Id, best.Person.Id, ct).ConfigureAwait(false);

            var members = await faces.GetByClusterAsync(record.Id, ct).ConfigureAwait(false);
            await faces.AssignAsync(
                [.. members
                    .Where(face => !face.IsUserDecided)
                    .Select(face => new FaceAssignment(face.Id, best.Person.Id, Assignment.Auto))],
                ct).ConfigureAwait(false);

            recognised++;
        }

        return recognised;
    }

    /// <summary>The unit-length average of a group's vectors.</summary>
    /// <remarks>
    /// Scaled back to unit length so it can be compared with a person's stored centroid using the
    /// same dot product everything else uses; an unnormalised average would score lower simply
    /// because averaging shortens a vector.
    /// </remarks>
    private static float[]? Centroid(IReadOnlyList<FaceEmbedding> vectors, List<int> group)
    {
        if (group.Count == 0)
        {
            return null;
        }

        var sum = new float[vectors[group[0]].Vector.Length];

        foreach (var position in group)
        {
            var vector = vectors[position].Vector;
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        var length = MathF.Sqrt(sum.Sum(v => v * v));
        if (length <= 0)
        {
            return null;
        }

        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] /= length;
        }

        return sum;
    }

    /// <summary>
    /// The face most representative of a group.
    /// </summary>
    /// <remarks>
    /// The member closest to all the others, rather than the average of them. An average is not a
    /// real face and cannot be shown on a tile; the medoid is an actual photograph, and being
    /// central it tends to be a clear, front-facing one.
    /// </remarks>
    private static int FindMedoid(IReadOnlyList<FaceEmbedding> vectors, List<int> group)
    {
        if (group.Count == 1)
        {
            return group[0];
        }

        var best = group[0];
        var bestScore = float.NegativeInfinity;

        foreach (var candidate in group)
        {
            var total = 0f;
            foreach (var other in group)
            {
                if (candidate != other)
                {
                    total += Dot(vectors[candidate].Vector, vectors[other].Vector);
                }
            }

            if (total > bestScore)
            {
                bestScore = total;
                best = candidate;
            }
        }

        return best;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private async Task<ClusterConstraints> LoadConstraintsAsync(
        IReadOnlyDictionary<FaceId, int> positions, CancellationToken ct)
    {
        var stored = await links.GetAllAsync(ct).ConfigureAwait(false);

        var mustLink = new List<(int, int)>();
        var cannotLink = new HashSet<(int, int)>();

        foreach (var link in stored)
        {
            // A decision about a face that is no longer in play, because its detector is inactive
            // or its photograph is gone, is kept in storage but has nowhere to apply here.
            if (!positions.TryGetValue(link.FaceA, out var a)
                || !positions.TryGetValue(link.FaceB, out var b))
            {
                continue;
            }

            var pair = a < b ? (a, b) : (b, a);

            if (link.Kind == FaceLinkKind.Must)
            {
                mustLink.Add(pair);
            }
            else
            {
                cannotLink.Add(pair);
            }
        }

        return new ClusterConstraints(mustLink, cannotLink);
    }
}

/// <param name="ClustersFormed">Groups large enough to become candidate people.</param>
/// <param name="FacesGrouped">Faces that landed in one of those groups.</param>
/// <param name="FacesUnsorted">Faces that matched nothing strongly enough.</param>
/// <param name="PeopleRecognised">Groups matched to somebody already named, needing no new name.</param>
public readonly record struct ClusteringResult(
    int ClustersFormed, int FacesGrouped, int FacesUnsorted, int PeopleRecognised = 0);
