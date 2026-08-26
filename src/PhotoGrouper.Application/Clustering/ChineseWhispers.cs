using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Application.Clustering;

/// <summary>
/// Groups faces into people by repeatedly letting each face adopt its neighbours' majority label.
/// </summary>
/// <remarks>
/// Chosen over the more familiar clustering algorithms because of what it does not require. The
/// number of people in a photo library is unknown, so anything that must be told how many groups
/// to find is unusable. Density-based methods avoid that but need a radius chosen in advance,
/// which behaves badly when one person appears in four hundred photographs and another in two.
///
/// This method needs neither. Each face starts in its own group and then repeatedly takes
/// whichever label carries the most weight among its neighbours. Densely connected faces converge
/// on a shared label within a few passes, and a face with no strong neighbours simply keeps its
/// own, which is the correct outcome for someone photographed once.
///
/// It lives in the application layer rather than beside the vector index because it is a rule
/// about how the product groups people, not a detail of how vectors are compared. It depends on
/// nothing but neighbour lists, which is also what makes it testable without any model.
/// </remarks>
public static class ChineseWhispers
{
    /// <summary>
    /// Passes over the graph before stopping.
    /// </summary>
    /// <remarks>
    /// Convergence is usually reached in well under ten; the cap only bounds the rare case where a
    /// pair of labels oscillates. The loop exits early when a pass changes nothing.
    /// </remarks>
    private const int MaximumIterations = 30;

    /// <summary>
    /// Assigns a cluster label to every face.
    /// </summary>
    /// <param name="neighbours">Each face's strongest neighbours, from the vector index.</param>
    /// <param name="constraints">User decisions that override similarity entirely.</param>
    /// <param name="seed">Fixes the visiting order so that a given input always clusters the same way.</param>
    /// <returns>A cluster label per face. Labels are arbitrary integers, not stable across runs.</returns>
    public static int[] Cluster(
        IReadOnlyList<Neighbour[]> neighbours,
        ClusterConstraints? constraints = null,
        int seed = 20260827)
    {
        ArgumentNullException.ThrowIfNull(neighbours);

        var count = neighbours.Count;
        var labels = new int[count];
        for (var i = 0; i < count; i++)
        {
            labels[i] = i;
        }

        if (count == 0)
        {
            return labels;
        }

        var edges = BuildEdges(neighbours, constraints);

        // Must-links are seeded here so the merged faces start out agreeing, which helps them pull
        // their neighbours in together. Seeding alone does not guarantee the constraint, because
        // propagation is free to separate them again on a later pass, so it is also enforced after
        // the loop. Both steps are needed: the first for quality, the second for correctness.
        var mustLink = constraints?.MustLink ?? [];
        foreach (var (a, b) in mustLink)
        {
            if (a >= 0 && a < count && b >= 0 && b < count)
            {
                Relabel(labels, from: Math.Max(labels[a], labels[b]), to: Math.Min(labels[a], labels[b]));
            }
        }

        var order = Enumerable.Range(0, count).ToArray();
        var random = new Random(seed);
        var weights = new Dictionary<int, float>();

        for (var iteration = 0; iteration < MaximumIterations; iteration++)
        {
            // Visiting order genuinely affects the outcome, so it is shuffled to avoid a bias
            // toward whatever order the database happened to return, and seeded so that the same
            // library always produces the same grouping. A user re-running clustering and getting
            // different people would have no way to tell that from a bug.
            Shuffle(order, random);

            var changed = 0;

            foreach (var node in order)
            {
                weights.Clear();

                foreach (var (neighbour, similarity) in edges[node])
                {
                    var label = labels[neighbour];
                    weights[label] = weights.GetValueOrDefault(label) + similarity;
                }

                if (weights.Count == 0)
                {
                    continue;
                }

                var best = labels[node];
                var bestWeight = float.NegativeInfinity;

                foreach (var (label, weight) in weights)
                {
                    // Ties broken toward the smaller label rather than by enumeration order, which
                    // for a dictionary is not something to rely on.
                    if (weight > bestWeight || (weight == bestWeight && label < best))
                    {
                        best = label;
                        bestWeight = weight;
                    }
                }

                if (best != labels[node])
                {
                    labels[node] = best;
                    changed++;
                }
            }

            if (changed == 0)
            {
                break;
            }
        }

        // Enforced after propagation, not merely before it. A user who has said two faces are the
        // same person must see that hold in the result, whatever the vectors say; seeding the
        // labels beforehand only makes it likely.
        foreach (var (a, b) in mustLink)
        {
            if (a >= 0 && a < count && b >= 0 && b < count && labels[a] != labels[b])
            {
                Relabel(labels, from: Math.Max(labels[a], labels[b]), to: Math.Min(labels[a], labels[b]));
            }
        }

        return Compact(labels);
    }

    /// <summary>
    /// Builds the neighbour graph, applying the user's cannot-link decisions.
    /// </summary>
    /// <remarks>
    /// A rejected pair has its edge removed outright rather than merely weakened. The user has
    /// stated these are different people; no similarity score should be able to overrule that,
    /// however high.
    /// </remarks>
    private static List<(int Neighbour, float Similarity)>[] BuildEdges(
        IReadOnlyList<Neighbour[]> neighbours, ClusterConstraints? constraints)
    {
        var count = neighbours.Count;
        var edges = new List<(int, float)>[count];
        for (var i = 0; i < count; i++)
        {
            edges[i] = [];
        }

        var forbidden = constraints?.CannotLink ?? ClusterConstraints.None.CannotLink;

        for (var node = 0; node < count; node++)
        {
            foreach (var neighbour in neighbours[node])
            {
                if (neighbour.Index == node || neighbour.Index < 0 || neighbour.Index >= count)
                {
                    continue;
                }

                var pair = neighbour.Index < node
                    ? (neighbour.Index, node)
                    : (node, neighbour.Index);

                if (forbidden.Contains(pair))
                {
                    continue;
                }

                // Added in both directions. The index returns each face's strongest matches, which
                // is not symmetric: a face in a crowd may list a portrait among its best matches
                // without appearing among that portrait's own. Leaving the graph directed would
                // make the result depend on which of the two happened to be visited first.
                edges[node].Add((neighbour.Index, neighbour.Similarity));
                edges[neighbour.Index].Add((node, neighbour.Similarity));
            }
        }

        return edges;
    }

    /// <summary>Renumbers labels to a dense range starting at zero.</summary>
    private static int[] Compact(int[] labels)
    {
        var mapping = new Dictionary<int, int>();
        var result = new int[labels.Length];

        for (var i = 0; i < labels.Length; i++)
        {
            if (!mapping.TryGetValue(labels[i], out var compacted))
            {
                compacted = mapping.Count;
                mapping[labels[i]] = compacted;
            }

            result[i] = compacted;
        }

        return result;
    }

    /// <summary>Moves every face carrying one label onto another.</summary>
    private static void Relabel(int[] labels, int from, int to)
    {
        if (from == to)
        {
            return;
        }

        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] == from)
            {
                labels[i] = to;
            }
        }
    }

    private static void Shuffle(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}

/// <summary>
/// Decisions the user made in review, which clustering must respect.
/// </summary>
/// <remarks>
/// The reason clustering can be re-run at all. Without these, every re-run would discard the
/// corrections a user had made by hand, and the app would appear to forget what it had been told.
/// Pairs are indices into the vector list handed to the clusterer, with the smaller index first.
/// </remarks>
public sealed record ClusterConstraints(
    IReadOnlyList<(int A, int B)> MustLink,
    IReadOnlySet<(int A, int B)> CannotLink)
{
    public static ClusterConstraints None { get; } = new([], new HashSet<(int, int)>());
}
