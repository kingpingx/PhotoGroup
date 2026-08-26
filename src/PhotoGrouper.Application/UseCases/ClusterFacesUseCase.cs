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
    IVectorIndex index,
    IClock clock)
{
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

        var vectors = new List<FaceEmbedding>();
        await foreach (var embedding in embeddings
                           .StreamByEmbedderAsync(embedderId, detectorId, ct)
                           .ConfigureAwait(false))
        {
            vectors.Add(embedding);
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

            foreach (var position in group)
            {
                assignments.Add(new FaceClusterAssignment(vectors[position].FaceId, clusterId));
            }
        }

        // Replaced wholesale: cluster identity carries no meaning between runs, and keeping the
        // previous set would leave every face pointing at a group that no longer describes it.
        // Names survive because they live on people, which this does not touch.
        await clusters.ReplaceAllAsync(detectorId, embedderId, records, ct).ConfigureAwait(false);
        await faces.SetClustersAsync(assignments, ct).ConfigureAwait(false);

        progress.Report(new ProgressUpdate("Grouping faces", vectors.Count, vectors.Count));
        return new ClusteringResult(records.Count, vectors.Count - singletons, singletons);
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
public readonly record struct ClusteringResult(int ClustersFormed, int FacesGrouped, int FacesUnsorted);
