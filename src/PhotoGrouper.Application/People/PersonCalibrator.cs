using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Application.People;

/// <summary>
/// Brings a person's derived data back in line with the faces they actually hold.
/// </summary>
/// <remarks>
/// A person carries two things nobody typed: the average of their face vectors, and the face shown
/// on their tile. Both are derived, both go stale the moment a face changes hands, and neither
/// announces that it has. The average is the worse of the two, because it is what a later grouping
/// run compares new faces against — a person whose average still describes photographs they no
/// longer have goes on collecting strangers who resemble those photographs.
///
/// This exists as one place because it used to be four. Three use cases each carried a private
/// method with the same body, and they had already drifted: one cleared the average when a person
/// was left with nothing, the other two returned early and left the old one in place. A fourth
/// caller was about to be added.
///
/// A collaborator rather than a use case, hence the name — nobody presses a button that calls this.
/// It sits beside the other Application services that are algorithms rather than operations.
/// </remarks>
public sealed class PersonCalibrator(
    IPersonRepository people,
    IFaceRepository faces,
    IEmbeddingRepository embeddings)
{
    public async Task<CalibrationResult> CalibrateAsync(
        PersonId personId, string detectorId, string embedderId, CancellationToken ct)
    {
        var person = await people.GetByIdAsync(personId, ct).ConfigureAwait(false);

        return person is null
            ? new CalibrationResult(personId, 0, false, null, false)
            : await CalibrateAsync(person, detectorId, embedderId, ct).ConfigureAwait(false);
    }

    /// <summary>The same, when the caller is already holding the person.</summary>
    public async Task<CalibrationResult> CalibrateAsync(
        Person person, string detectorId, string embedderId, CancellationToken ct)
    {
        var assigned = await faces.GetByPersonAsync(person.Id, detectorId, ct).ConfigureAwait(false);
        var previousCover = person.CoverFaceId;

        var vectors = assigned.Count == 0
            ? []
            : await embeddings
                .GetManyAsync([.. assigned.Select(face => face.Id)], embedderId, ct)
                .ConfigureAwait(false);

        var byFace = vectors.ToDictionary(v => v.FaceId, v => v.Vector);
        var centroid = UnitMean(assigned, byFace);

        person.UpdateCentroid(centroid);
        person.SetCoverFace(ChooseCover(assigned, byFace, centroid, previousCover));

        await people.UpdateAsync(person, ct).ConfigureAwait(false);

        return new CalibrationResult(
            person.Id,
            assigned.Count,
            centroid is not null,
            person.CoverFaceId,
            person.CoverFaceId != previousCover);
    }

    /// <summary>Calibrates several people, for a repair that sweeps the whole library.</summary>
    public async Task<IReadOnlyList<CalibrationResult>> CalibrateAsync(
        IReadOnlyList<PersonId> personIds, string detectorId, string embedderId, CancellationToken ct)
    {
        var results = new List<CalibrationResult>(personIds.Count);

        foreach (var personId in personIds)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CalibrateAsync(personId, detectorId, embedderId, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// The unit-length mean of this person's vectors, or null when they have none.
    /// </summary>
    /// <remarks>
    /// Null rather than leaving the previous value, which is the behaviour two of the three
    /// replaced copies had. A person emptied of faces who keeps an average is worse than one with
    /// none: recognition treats them as a live identity and files whoever resembles their former
    /// photographs under their name.
    ///
    /// Scaled back to unit length so it can be compared with individual faces using the same dot
    /// product everything else uses; an unnormalised mean would score lower simply for being an
    /// average.
    /// </remarks>
    private static float[]? UnitMean(
        IReadOnlyList<Face> assigned, IReadOnlyDictionary<FaceId, float[]> byFace)
    {
        float[]? sum = null;
        var counted = 0;

        foreach (var face in assigned)
        {
            if (!byFace.TryGetValue(face.Id, out var vector))
            {
                continue;
            }

            sum ??= new float[vector.Length];
            for (var i = 0; i < vector.Length; i++)
            {
                sum[i] += vector[i];
            }

            counted++;
        }

        if (sum is null || counted == 0)
        {
            return null;
        }

        var length = MathF.Sqrt(sum.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] /= length;
            }
        }

        return sum;
    }

    /// <summary>
    /// The face to show on this person's tile.
    /// </summary>
    /// <remarks>
    /// Keeps the existing cover whenever the person still owns that face. A tile whose picture
    /// changes every time something is corrected is unsettling, and the user may well have chosen
    /// it; stability is worth more here than picking the theoretical best each time.
    ///
    /// Otherwise the face closest to the person's own average — the most typical of them, which is
    /// what a cover is for. Better than simply the largest, which may be a blurred profile that
    /// happens to fill the frame. It costs one dot product per face and no extra reads, because the
    /// vectors are already in hand.
    ///
    /// With no vectors to compare, the largest face and then the detector's confidence, which is
    /// the rule both screens already fall back to when a stored cover turns out to be nobody's.
    /// </remarks>
    private static FaceId? ChooseCover(
        IReadOnlyList<Face> assigned,
        IReadOnlyDictionary<FaceId, float[]> byFace,
        float[]? centroid,
        FaceId? previous)
    {
        if (assigned.Count == 0)
        {
            return null;
        }

        if (previous is { } cover && assigned.Any(face => face.Id == cover))
        {
            return cover;
        }

        if (centroid is not null)
        {
            var nearest = assigned
                .Where(face => byFace.ContainsKey(face.Id))
                .OrderByDescending(face => Dot(byFace[face.Id], centroid))
                .FirstOrDefault();

            if (nearest is not null)
            {
                return nearest.Id;
            }
        }

        return assigned
            .OrderByDescending(face => face.Box.SmallestSide)
            .ThenByDescending(face => face.Box.Score)
            .First().Id;
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

/// <param name="FaceCount">How many faces the person holds after calibration.</param>
/// <param name="HasCentroid">False when they hold no vectors, so recognition must skip them.</param>
/// <param name="CoverChanged">True when the tile will now show a different face.</param>
public readonly record struct CalibrationResult(
    PersonId PersonId, int FaceCount, bool HasCentroid, FaceId? CoverFaceId, bool CoverChanged);
