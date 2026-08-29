using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Gives a name to a group of faces, creating the person if they are new.
/// </summary>
/// <remarks>
/// The core interaction of the whole application: one group, one name, and every photograph of
/// that person becomes findable at once. It is also the only operation here that creates
/// something no algorithm could regenerate, which is why it takes care over the cases where a
/// user names two different groups the same thing.
/// </remarks>
public sealed class NamePersonUseCase(
    IPersonRepository people,
    IClusterRepository clusters,
    IFaceRepository faces,
    IEmbeddingRepository embeddings,
    IClock clock)
{
    /// <summary>
    /// Names a cluster.
    /// </summary>
    /// <remarks>
    /// Naming a second group with an existing name is treated as a merge rather than an error.
    /// A person photographed across several years, or in very different lighting, frequently comes
    /// out as more than one group, and typing the same name again is the natural way for a user to
    /// say those are the same person. Rejecting it would leave them with no way to express it.
    /// </remarks>
    public async Task<NamingResult> ExecuteAsync(ClusterId clusterId, string name, CancellationToken ct)
    {
        if (!PersonName.TryCreate(name, out var personName, out var error))
        {
            return NamingResult.Invalid(error!);
        }

        var cluster = await clusters.GetByIdAsync(clusterId, ct).ConfigureAwait(false);
        if (cluster is not { } record)
        {
            return NamingResult.Invalid("That group no longer exists. Re-run grouping and try again.");
        }

        var members = await faces.GetByClusterAsync(clusterId, ct).ConfigureAwait(false);

        // Faces the user has already decided about by hand are not moved by naming their group,
        // which means a group made up entirely of them has nothing to give the name to. Checked
        // before anything is written, because the alternative is what it used to do: create the
        // person, point the group at them, assign nothing, and report success. The user was left
        // with a name that had no photographs and no indication of why.
        //
        // It happens through an ordinary sequence. Moving a photograph to somebody else marks that
        // face as decided but leaves its group unnamed, so the group comes back round as one to
        // name, and naming it produces the empty person.
        var assignable = members.Where(face => !face.IsUserDecided).ToList();

        if (assignable.Count == 0)
        {
            var owner = await DescribeOwnerAsync(members, ct).ConfigureAwait(false);

            return NamingResult.Invalid(
                "Every face in this group has already been placed by hand"
                + (owner is null ? string.Empty : $", on {owner}")
                + ". Naming it would create somebody with no photographs. Open that person to move "
                + "the face instead.");
        }

        var existing = await people.GetByNameAsync(personName, ct).ConfigureAwait(false);
        var merged = existing is not null;

        var person = existing ?? new Person(
            PersonId.New(), personName, clock.UtcNow, coverFaceId: record.MedoidFaceId);

        if (existing is null)
        {
            await people.AddAsync(person, ct).ConfigureAwait(false);
        }

        await clusters.SetPersonAsync(clusterId, person.Id, ct).ConfigureAwait(false);

        // Marked automatic rather than confirmed. The user has named the group, not inspected
        // every face in it, and treating the whole group as hand-verified would make later
        // corrections impossible to distinguish from the original guess.
        await faces.AssignAsync(
            [.. assignable.Select(face => new FaceAssignment(face.Id, person.Id, Assignment.Auto))],
            ct).ConfigureAwait(false);

        await UpdateCentroidAsync(person, record.DetectorId, record.EmbedderId, ct).ConfigureAwait(false);

        // Naming somebody is a statement about who they are, not only about this one group, so the
        // remaining groups are checked against them straight away. A person photographed in very
        // different lighting, or across years, routinely comes out as several groups; without this
        // the user is asked to name the same person over and over, and only a full re-grouping
        // would join them up.
        var absorbed = await AbsorbMatchingGroupsAsync(
            person, clusterId, record.DetectorId, record.EmbedderId, ct).ConfigureAwait(false);

        // What was actually assigned, not what the group contained. Counting the members would
        // report photographs to a person who never received them.
        var total = assignable.Count + absorbed.Faces;

        return NamingResult.Success(person.Id, personName, total, merged, absorbed.Groups);
    }

    public async Task<NamingResult> RenameAsync(PersonId personId, string name, CancellationToken ct)
    {
        if (!PersonName.TryCreate(name, out var personName, out var error))
        {
            return NamingResult.Invalid(error!);
        }

        var person = await people.GetByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return NamingResult.Invalid("That person no longer exists.");
        }

        var clash = await people.GetByNameAsync(personName, ct).ConfigureAwait(false);
        if (clash is not null && clash.Id != personId)
        {
            return NamingResult.Invalid(
                $"Someone called {personName} already exists. Merge them instead of renaming.");
        }

        person.Rename(personName);
        await people.UpdateAsync(person, ct).ConfigureAwait(false);

        return NamingResult.Success(person.Id, personName, 0, merged: false);
    }

    /// <summary>
    /// Attaches any other unnamed group that looks like this person.
    /// </summary>
    /// <remarks>
    /// Compares each remaining group's average vector against the person's, at the same bar
    /// clustering uses when it recognises somebody already named. That bar is higher than the one
    /// used between individual faces: an average cancels out the lighting and angle of any single
    /// photograph, so more can be asked of it, and the cost of being wrong is worse — quietly
    /// filing a stranger's photographs under somebody's name rather than leaving a group unnamed.
    ///
    /// Faces the user has decided about by hand are left alone, as everywhere else.
    /// </remarks>
    private async Task<(int Groups, int Faces)> AbsorbMatchingGroupsAsync(
        Person person,
        ClusterId justNamed,
        string detectorId,
        string embedderId,
        CancellationToken ct)
    {
        if (person.Centroid is not { Length: > 0 } centroid)
        {
            return (0, 0);
        }

        var candidates = (await clusters.GetAllAsync(detectorId, embedderId, ct).ConfigureAwait(false))
            .Where(c => c.PersonId is null && c.Id != justNamed)
            .ToList();

        if (candidates.Count == 0)
        {
            return (0, 0);
        }

        // Read once and indexed, rather than a query per group. A library with many unnamed groups
        // would otherwise issue a query for every one of them each time a name was typed.
        var vectors = new Dictionary<FaceId, float[]>();
        await foreach (var embedding in embeddings
                           .StreamByEmbedderAsync(embedderId, detectorId, ct)
                           .ConfigureAwait(false))
        {
            vectors[embedding.FaceId] = embedding.Vector;
        }

        var groups = 0;
        var absorbedFaces = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var members = await faces.GetByClusterAsync(candidate.Id, ct).ConfigureAwait(false);
            var known = members
                .Select(face => vectors.TryGetValue(face.Id, out var v) ? v : null)
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();

            if (known.Count == 0 || Similarity(Average(known), centroid) < ClusterFacesUseCase.PersonMatchThreshold)
            {
                continue;
            }

            var assignable = members.Where(face => !face.IsUserDecided).ToList();
            if (assignable.Count == 0)
            {
                continue;
            }

            await clusters.SetPersonAsync(candidate.Id, person.Id, ct).ConfigureAwait(false);
            await faces.AssignAsync(
                [.. assignable.Select(face => new FaceAssignment(face.Id, person.Id, Assignment.Auto))],
                ct).ConfigureAwait(false);

            groups++;
            absorbedFaces += assignable.Count;
        }

        if (groups > 0)
        {
            // The person now covers more faces than the centroid was computed from, so it is
            // recomputed rather than left describing only the first group.
            await UpdateCentroidAsync(person, detectorId, embedderId, ct).ConfigureAwait(false);
        }

        return (groups, absorbedFaces);
    }

    /// <summary>
    /// Names the person a group's faces already belong to, when they all belong to one.
    /// </summary>
    /// <remarks>
    /// Only to make the refusal above actionable. "This group is already spoken for" leaves a user
    /// hunting; naming who has it tells them where to go and undo it.
    /// </remarks>
    private async Task<string?> DescribeOwnerAsync(IReadOnlyList<Face> members, CancellationToken ct)
    {
        var owners = members
            .Select(face => face.PersonId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (owners.Count != 1)
        {
            return null;
        }

        var owner = await people.GetByIdAsync(owners[0], ct).ConfigureAwait(false);
        return owner?.Name.Value;
    }

    /// <summary>The unit-length average of a set of vectors.</summary>
    private static float[] Average(IReadOnlyList<float[]> vectors)
    {
        var sum = new float[vectors[0].Length];

        foreach (var vector in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vector[i];
            }
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

    private static float Similarity(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Recomputes the average of a person's face vectors.
    /// </summary>
    /// <remarks>
    /// A cache used to place newly scanned faces without re-clustering the library: a new face is
    /// compared against a few hundred of these rather than against every face that exists. It is
    /// derived, so a failure to update it costs accuracy on the next scan and nothing more.
    /// </remarks>
    private async Task UpdateCentroidAsync(
        Person person, string detectorId, string embedderId, CancellationToken ct)
    {
        var assigned = await faces.GetByPersonAsync(person.Id, detectorId, ct).ConfigureAwait(false);

        float[]? sum = null;
        var counted = 0;

        foreach (var face in assigned)
        {
            var vector = await embeddings.GetAsync(face.Id, embedderId, ct).ConfigureAwait(false);
            if (vector is null)
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
            return;
        }

        // Scaled back to unit length so the centroid can be compared with individual faces using
        // the same dot product everything else uses.
        var length = MathF.Sqrt(sum.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] /= length;
            }
        }

        person.UpdateCentroid(sum);
        await people.UpdateAsync(person, ct).ConfigureAwait(false);
    }

}

/// <param name="Merged">True when the name already existed and this group joined that person.</param>
/// <param name="GroupsAbsorbed">Other unnamed groups recognised as the same person and attached.</param>
public readonly record struct NamingResult(
    bool IsSuccess,
    PersonId PersonId,
    string Name,
    int FacesAssigned,
    bool Merged,
    string? Error,
    int GroupsAbsorbed = 0)
{
    public static NamingResult Success(PersonId id, PersonName name, int faces, bool merged, int absorbed = 0) =>
        new(true, id, name.Value, faces, merged, null, absorbed);

    public static NamingResult Invalid(string error) =>
        new(false, default, string.Empty, 0, false, error);
}
