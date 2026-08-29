namespace PhotoGrouper.Application.Clustering;

/// <summary>
/// Groups indices into sets, joining them as pairs are found to match.
/// </summary>
/// <remarks>
/// Needed wherever "alike" is judged pair by pair, because being alike is not transitive at the
/// edges: frame one may match frame two, and frame two frame three, without one and three being
/// within the threshold of each other. A burst is one set, so the pairs are unioned rather than
/// each becoming a group of its own and asking the user the same question twice.
///
/// Shared rather than copied. Three separate features now group things this way — photographs that
/// are the same picture, names that are the same person, faces that are the same moment — and each
/// arrived at the identical fifteen lines with the identical comment above them.
/// </remarks>
internal sealed class UnionFind(int count)
{
    private readonly int[] _parent = [.. Enumerable.Range(0, count)];

    /// <summary>The representative of the set this index belongs to.</summary>
    /// <remarks>
    /// Halves the path on the way up, so a long chain of joins does not make later lookups walk it
    /// end to end every time.
    /// </remarks>
    public int Find(int index)
    {
        while (_parent[index] != index)
        {
            _parent[index] = _parent[_parent[index]];
            index = _parent[index];
        }

        return index;
    }

    public void Join(int a, int b)
    {
        var rootA = Find(a);
        var rootB = Find(b);

        if (rootA != rootB)
        {
            _parent[rootB] = rootA;
        }
    }

    /// <summary>
    /// The members of every set holding more than one index, keyed by representative.
    /// </summary>
    /// <remarks>
    /// Here rather than in each caller because all three did the same thing with the result: build
    /// a dictionary from root to members, then drop the singletons. A set of one is not a set of
    /// duplicates, and every caller had to remember to say so.
    /// </remarks>
    public IReadOnlyList<List<int>> Sets(int minimumSize = 2)
    {
        var members = new Dictionary<int, List<int>>();

        for (var i = 0; i < count; i++)
        {
            var root = Find(i);
            if (!members.TryGetValue(root, out var list))
            {
                list = [];
                members[root] = list;
            }

            list.Add(i);
        }

        return [.. members.Values.Where(set => set.Count >= minimumSize)];
    }
}
