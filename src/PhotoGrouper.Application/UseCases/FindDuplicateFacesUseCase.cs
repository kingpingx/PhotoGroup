using PhotoGrouper.Application.Clustering;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Finds faces of one person that are the same moment.
/// </summary>
/// <remarks>
/// A burst leaves a person holding several frames of one instant: the same pose, the same light, a
/// blink apart. They are all genuinely that person, so nothing else in the application has any
/// reason to separate them, and they accumulate quietly until somebody has forty photographs of
/// which eight are worth keeping.
///
/// This is not the question the library's duplicate-photo tool answers, and cannot be answered with
/// the same machinery. That tool compares whole photographs by fingerprint; here the two
/// photographs may be a wide shot and a close-up, sharing almost no pixels while holding the same
/// instant of the same face. So the comparison is between the face vectors, which already exist,
/// are already unit length, and are already what this application trusts to say two faces are one
/// person.
///
/// It only ever reports. Removing is a different decision and a different piece of code.
/// </remarks>
public sealed class FindDuplicateFacesUseCase(
    IFaceRepository faces,
    IEmbeddingRepository embeddings,
    IPhotoSignatureRepository signatures)
{
    /// <summary>
    /// How alike two faces must be to be called the same moment rather than the same person.
    /// </summary>
    /// <remarks>
    /// Every face compared here already belongs to one person, so "is this the same person" is not
    /// the question and the ordinary same-person bar would match everything.
    ///
    /// Measured over a real library of twenty-two embedded faces, the pairs fall into two clusters
    /// with nothing in between. Copies of one picture — byte-identical files, and the same image
    /// re-saved in another format — score 0.88 to 1.00. The same person in a genuinely different
    /// photograph scores 0.43 and 0.66. Two different people never exceed 0.24, which is not the
    /// question here but is worth knowing: there is no risk of this reaching across identities.
    ///
    /// So the line belongs somewhere in the empty stretch between 0.66 and 0.88, and this sits
    /// deliberately above it rather than in the middle. The two mistakes are not equal: missing one
    /// leaves a person with an extra photograph, and inventing one offers to strip them down to a
    /// single picture, which is the opposite of what a person is for.
    ///
    /// What that sample did not contain is a true burst — several frames of a moving subject
    /// seconds apart, rather than copies of one frame. Those should score between the two clusters
    /// and nearer the top, but that is reasoning and the numbers above are measurement, so the
    /// distinction is stated rather than blurred. If bursts turn out to be missed, the evidence for
    /// lowering this is a library that contains some.
    ///
    /// Passed in rather than read directly, so the screen can offer a stricter or looser setting
    /// without this constant pretending to be the only answer.
    /// </remarks>
    public const float DefaultMinimumSimilarity = 0.92f;

    public async Task<IReadOnlyList<DuplicateFaceSet>> ExecuteAsync(
        PersonId personId,
        string detectorId,
        string embedderId,
        float minimumSimilarity,
        CancellationToken ct)
    {
        var assigned = await faces.GetByPersonAsync(personId, detectorId, ct).ConfigureAwait(false);
        if (assigned.Count < 2)
        {
            return [];
        }

        // A face with no vector is dropped rather than compared as a zero. Zero is nothing-alike to
        // everything, which is harmless, but keeping it would put a face in the answer that this
        // has not actually judged.
        var members = new List<Face>(assigned.Count);
        var vectors = new List<float[]>(assigned.Count);

        foreach (var face in assigned)
        {
            ct.ThrowIfCancellationRequested();

            if (await embeddings.GetAsync(face.Id, embedderId, ct).ConfigureAwait(false) is { } vector)
            {
                members.Add(face);
                vectors.Add(vector);
            }
        }

        if (members.Count < 2)
        {
            return [];
        }

        var union = new UnionFind(members.Count);
        var closest = new float[members.Count];

        // Every pair compared directly rather than through the vector index. That index keeps only
        // each face's strongest few matches, so a burst of thirty frames would be truncated at
        // whatever that number happened to be, and here every pair above the bar matters. One
        // person holds tens of faces, so this is a few thousand dot products.
        for (var i = 0; i < members.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            for (var j = i + 1; j < members.Count; j++)
            {
                var similarity = Similarity(vectors[i], vectors[j]);
                if (similarity < minimumSimilarity)
                {
                    continue;
                }

                union.Join(i, j);

                // Held per face so each tile can say how sure this is about itself. A set built
                // through a chain contains faces that never matched each other directly, and one
                // number for the whole set would overstate its weakest link.
                closest[i] = Math.Max(closest[i], similarity);
                closest[j] = Math.Max(closest[j], similarity);
            }
        }

        var sets = union.Sets();
        if (sets.Count == 0)
        {
            return [];
        }

        var sharpness = await SharpnessByPhotoAsync(ct).ConfigureAwait(false);
        var results = new List<DuplicateFaceSet>(sets.Count);

        foreach (var indices in sets)
        {
            var ordered = indices
                .Select(index => (Face: members[index], Similarity: closest[index]))
                .OrderByDescending(m => m.Face.Assignment == Assignment.Confirmed)
                .ThenByDescending(m => m.Face.FacePixels)
                .ThenByDescending(m => sharpness.GetValueOrDefault(m.Face.PhotoId))
                .ThenByDescending(m => m.Face.Box.Score)
                .ThenBy(m => m.Face.Id.Value)
                .Select(m => new DuplicateFace(m.Face.Id, m.Face.PhotoId, m.Similarity))
                .ToList();

            results.Add(new DuplicateFaceSet(ordered));
        }

        // Biggest sets first: a burst of eight is where the photographs are, and where one decision
        // covers the most of them for the same moment of attention.
        return [.. results.OrderByDescending(set => set.Members.Count)];
    }

    /// <summary>
    /// How much detail each photograph carries, where that is already known.
    /// </summary>
    /// <remarks>
    /// The same number the duplicate-photo tool uses to choose between burst frames, and it is
    /// present exactly when it is most needed, because somebody with bursts has usually run that
    /// tool. Absent for a photograph never fingerprinted, which then falls through to the next rule
    /// rather than being treated as the blurriest.
    /// </remarks>
    private async Task<Dictionary<PhotoId, double>> SharpnessByPhotoAsync(CancellationToken ct)
    {
        var all = await signatures.GetAllAsync(ct).ConfigureAwait(false);
        var byPhoto = new Dictionary<PhotoId, double>(all.Count);

        foreach (var signature in all)
        {
            byPhoto[signature.PhotoId] = signature.Sharpness;
        }

        return byPhoto;
    }

    /// <remarks>
    /// A plain dot product, because the stored vectors are unit length. That is a stated contract of
    /// the embedding port rather than an assumption made here.
    /// </remarks>
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

/// <summary>Faces of one person that are the same moment, the one worth keeping first.</summary>
public sealed record DuplicateFaceSet(IReadOnlyList<DuplicateFace> Members)
{
    /// <summary>
    /// The face suggested for keeping.
    /// </summary>
    /// <remarks>
    /// A face the user confirmed wins outright: they looked at it and said it was this person, and
    /// no measurement earns the right to propose removing it in favour of one the application
    /// guessed. Failing that the largest face, which carries the most detail, then the sharpest
    /// photograph, then the detector's own confidence.
    /// </remarks>
    public DuplicateFace Keeper => Members[0];

    /// <summary>How many faces this set would give up, keeping one.</summary>
    public int ExtraCount => Members.Count - 1;
}

/// <param name="Similarity">How alike this face is to the nearest other in its set, from -1 to 1.</param>
public readonly record struct DuplicateFace(FaceId FaceId, PhotoId PhotoId, float Similarity);
