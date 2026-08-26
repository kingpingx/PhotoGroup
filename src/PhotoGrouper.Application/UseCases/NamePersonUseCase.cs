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

        var existing = await people.GetByNameAsync(personName, ct).ConfigureAwait(false);
        var merged = existing is not null;

        var person = existing ?? new Person(
            PersonId.New(), personName, clock.UtcNow, coverFaceId: record.MedoidFaceId);

        if (existing is null)
        {
            await people.AddAsync(person, ct).ConfigureAwait(false);
        }

        await clusters.SetPersonAsync(clusterId, person.Id, ct).ConfigureAwait(false);

        var members = await faces.GetByClusterAsync(clusterId, ct).ConfigureAwait(false);

        // Marked automatic rather than confirmed. The user has named the group, not inspected
        // every face in it, and treating the whole group as hand-verified would make later
        // corrections impossible to distinguish from the original guess.
        await faces.AssignAsync(
            [.. members
                .Where(face => !face.IsUserDecided)
                .Select(face => new FaceAssignment(face.Id, person.Id, Assignment.Auto))],
            ct).ConfigureAwait(false);

        await UpdateCentroidAsync(person, record.DetectorId, record.EmbedderId, ct).ConfigureAwait(false);

        return NamingResult.Success(person.Id, personName, members.Count, merged);
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
public readonly record struct NamingResult(
    bool IsSuccess,
    PersonId PersonId,
    string Name,
    int FacesAssigned,
    bool Merged,
    string? Error)
{
    public static NamingResult Success(PersonId id, PersonName name, int faces, bool merged) =>
        new(true, id, name.Value, faces, merged, null);

    public static NamingResult Invalid(string error) =>
        new(false, default, string.Empty, 0, false, error);
}
