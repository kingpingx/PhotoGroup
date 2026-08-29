using PhotoGrouper.Application.Clustering;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Finds people who have been named more than once.
/// </summary>
/// <remarks>
/// Grouping splits a person routinely — different lighting, a different haircut, a decade apart —
/// and naming one group says nothing about the others unless they were still unnamed at the time.
/// Anyone who worked through their library from the top has therefore created the same person
/// twice: named once as "Alice", and again later as "Alice 2" or as a number, with no way to
/// notice short of scrolling the whole screen and recognising a face twice.
///
/// This compares people with each other, which nothing else in the application does. Naming
/// compares a group against the named; clustering compares faces against faces. The gap between
/// those two is exactly where a person gets named twice, and it widens every time a library grows.
/// </remarks>
public sealed class FindDuplicatePeopleUseCase(
    IPersonRepository people,
    IFaceRepository faces)
{
    /// <summary>
    /// How alike two people's average faces must be to be offered as one person.
    /// </summary>
    /// <remarks>
    /// The same bar the rest of the application uses to say a set of faces belongs to somebody
    /// already named, so this cannot contradict what naming itself would have done. It is a high
    /// bar deliberately: an average taken over several photographs has cancelled out the lighting
    /// and angle of any one of them, so more can be asked of it than of a single face.
    ///
    /// Nothing here merges anything. Being wrong costs a suggestion the user declines, which is why
    /// the threshold can sit at the same place rather than being pushed higher out of caution.
    /// </remarks>
    public const float DefaultMinimumSimilarity = ClusterFacesUseCase.PersonMatchThreshold;

    public async Task<IReadOnlyList<DuplicatePersonGroup>> ExecuteAsync(
        string detectorId, float minimumSimilarity, CancellationToken ct)
    {
        // Only people whose average face is known. Somebody named before their photographs were
        // embedded has no vector to compare, and guessing from their name would match every pair of
        // people a user numbered rather than named.
        var candidates = (await people.GetAllAsync(ct).ConfigureAwait(false))
            .Where(person => person.Centroid is { Length: > 0 })
            .ToList();

        if (candidates.Count < 2)
        {
            return [];
        }

        var union = new UnionFind(candidates.Count);
        var closest = new float[candidates.Count];

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            for (var j = i + 1; j < candidates.Count; j++)
            {
                var similarity = Similarity(candidates[i].Centroid!, candidates[j].Centroid!);
                if (similarity < minimumSimilarity)
                {
                    continue;
                }

                union.Join(i, j);

                // Kept per person so each tile can say how sure this is. A set formed through a
                // chain contains pairs that never matched each other, and reporting one number for
                // the whole set would overstate the weakest link.
                closest[i] = Math.Max(closest[i], similarity);
                closest[j] = Math.Max(closest[j], similarity);
            }
        }

        var groups = new List<DuplicatePersonGroup>();

        foreach (var indices in union.Sets())
        {
            var described = new List<DuplicatePerson>(indices.Count);

            foreach (var index in indices)
            {
                var person = candidates[index];
                var assigned = await faces
                    .GetByPersonAsync(person.Id, detectorId, ct)
                    .ConfigureAwait(false);

                described.Add(new DuplicatePerson(
                    person.Id,
                    person.Name.Value,
                    assigned.Count,
                    assigned.Select(face => face.PhotoId).Distinct().Count(),
                    ChooseCoverFace(person.CoverFaceId, assigned),
                    closest[index]));
            }

            // The person with the most photographs first. Theirs is the name most likely to have
            // been given deliberately rather than typed to clear a tile off the screen, and merging
            // into it moves the fewest faces.
            groups.Add(new DuplicatePersonGroup(
                [.. described
                    .OrderByDescending(p => p.PhotoCount)
                    .ThenByDescending(p => p.FaceCount)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)]));
        }

        return [.. groups
            .OrderByDescending(g => g.Members.Count)
            .ThenByDescending(g => g.Members.Sum(m => m.PhotoCount))];
    }

    /// <remarks>
    /// The person's chosen cover where they have one and it is still theirs, and otherwise their
    /// largest face. A tile in this list has to be recognisable at a glance: the whole decision is
    /// whether two faces are the same person, and a name cannot answer that.
    /// </remarks>
    private static FaceId? ChooseCoverFace(FaceId? preferred, IReadOnlyList<Face> assigned)
    {
        if (assigned.Count == 0)
        {
            return null;
        }

        if (preferred is { } cover && assigned.Any(face => face.Id == cover))
        {
            return cover;
        }

        return assigned
            .OrderByDescending(face => face.Box.SmallestSide)
            .ThenByDescending(face => face.Box.Score)
            .First().Id;
    }

    private static float Similarity(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

}

/// <summary>A set of names that appear to belong to one person, best-established first.</summary>
public sealed record DuplicatePersonGroup(IReadOnlyList<DuplicatePerson> Members)
{
    /// <summary>The name suggested to keep.</summary>
    public DuplicatePerson Best => Members[0];

    /// <summary>Photographs that would end up under one name.</summary>
    public int CombinedPhotoCount => Members.Sum(m => m.PhotoCount);
}

/// <param name="CoverFaceId">A face to show, so the user can judge by the face rather than the name.</param>
/// <param name="Similarity">How alike this person is to the nearest other in the set, from -1 to 1.</param>
public readonly record struct DuplicatePerson(
    PersonId Id, string Name, int FaceCount, int PhotoCount, FaceId? CoverFaceId, float Similarity);
