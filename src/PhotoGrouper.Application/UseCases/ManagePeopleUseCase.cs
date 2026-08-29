using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Correcting the grouping after the fact: renaming somebody, removing them, and taking
/// photographs off a person they are not in.
/// </summary>
/// <remarks>
/// Grouping by face is never going to be right first time. It confuses siblings, splits a person
/// photographed a decade apart, and occasionally decides a patterned cushion is a face. Naming a
/// group is only useful if the result can then be corrected, so these operations are not polish;
/// they are what makes the automatic part safe to trust.
///
/// The distinction that matters throughout is between removing a name and rejecting a face.
/// Deleting a person says the name was wrong and releases their faces to be grouped again.
/// Removing a photograph from a person says that particular face is not them, which is a judgement
/// no later re-grouping is allowed to overturn.
/// </remarks>
public sealed class ManagePeopleUseCase(
    IPersonRepository people,
    IFaceRepository faces,
    IClusterRepository clusters,
    IEmbeddingRepository embeddings,
    IPhotoReader photos)
{
    /// <summary>Every photograph a person appears in, for review.</summary>
    public async Task<IReadOnlyList<PersonPhoto>> GetPhotosAsync(
        PersonId personId, string detectorId, CancellationToken ct)
    {
        var assigned = await faces.GetByPersonAsync(personId, detectorId, ct).ConfigureAwait(false);
        var results = new List<PersonPhoto>(assigned.Count);

        foreach (var face in assigned)
        {
            var photo = await photos.GetByIdAsync(face.PhotoId, ct).ConfigureAwait(false);
            if (photo is not null)
            {
                results.Add(new PersonPhoto(face.Id, photo.Id, photo.Path, face.Box, face.Assignment));
            }
        }

        return results;
    }

    /// <summary>Everybody except the person being looked at, for moving faces to.</summary>
    public async Task<IReadOnlyList<PersonSummary>> GetOtherPeopleAsync(
        PersonId excluding, CancellationToken ct)
    {
        var all = await people.GetAllAsync(ct).ConfigureAwait(false);

        return [.. all
            .Where(person => person.Id != excluding)
            .Select(person => new PersonSummary(person.Id, person.Name.Value))];
    }

    /// <summary>
    /// Moves photographs from one person to another.
    /// </summary>
    /// <remarks>
    /// Recorded as confirmed rather than automatic. Someone who has looked at a photograph and said
    /// it belongs to a different person has made a judgement, and the next grouping run must not
    /// quietly move it back; an automatic assignment carries no such protection.
    ///
    /// Both people's average vectors are recomputed afterwards. Leaving them stale would mean the
    /// next grouping compares new faces against an average that still includes photographs the
    /// person no longer has, and against one that omits photographs they now do.
    /// </remarks>
    public async Task<PersonActionResult> MoveFacesAsync(
        PersonId fromPersonId,
        PersonId toPersonId,
        IReadOnlyList<FaceId> faceIds,
        string detectorId,
        string embedderId,
        CancellationToken ct)
    {
        if (faceIds.Count == 0)
        {
            return PersonActionResult.Failed("Nothing was selected.");
        }

        if (fromPersonId == toPersonId)
        {
            return PersonActionResult.Failed("Those photos already belong to that person.");
        }

        var source = await people.GetByIdAsync(fromPersonId, ct).ConfigureAwait(false);
        var target = await people.GetByIdAsync(toPersonId, ct).ConfigureAwait(false);

        if (source is null || target is null)
        {
            return PersonActionResult.Failed("One of those people no longer exists.");
        }

        await faces.AssignAsync(
            [.. faceIds.Select(id => new FaceAssignment(id, toPersonId, Assignment.Confirmed))],
            ct).ConfigureAwait(false);

        await UpdateCentroidAsync(source, detectorId, embedderId, ct).ConfigureAwait(false);
        await UpdateCentroidAsync(target, detectorId, embedderId, ct).ConfigureAwait(false);

        return PersonActionResult.Succeeded(
            $"Moved {faceIds.Count:N0} photo(s) from {source.Name} to {target.Name}.");
    }

    /// <summary>
    /// Recomputes the average of a person's face vectors.
    /// </summary>
    /// <remarks>
    /// The average is what a later grouping run compares new groups against, so it has to follow
    /// any change in who owns which face. A person left with nothing keeps no average at all,
    /// rather than one describing photographs they no longer have.
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
            person.UpdateCentroid(null);
            await people.UpdateAsync(person, ct).ConfigureAwait(false);
            return;
        }

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

    public async Task<PersonActionResult> RenameAsync(PersonId personId, string name, CancellationToken ct)
    {
        if (!PersonName.TryCreate(name, out var personName, out var error))
        {
            return PersonActionResult.Failed(error!);
        }

        var person = await people.GetByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return PersonActionResult.Failed("That person no longer exists.");
        }

        var clash = await people.GetByNameAsync(personName, ct).ConfigureAwait(false);
        if (clash is not null && clash.Id != personId)
        {
            return PersonActionResult.Failed(
                $"Someone called {personName} already exists. Name this group {personName} instead, "
                + "which will merge the two.");
        }

        var previous = person.Name.Value;
        person.Rename(personName);
        await people.UpdateAsync(person, ct).ConfigureAwait(false);

        return PersonActionResult.Succeeded($"Renamed {previous} to {personName}.");
    }

    /// <summary>
    /// Removes a person, releasing their faces.
    /// </summary>
    /// <remarks>
    /// The faces are detached rather than rejected, and their groups become unnamed again rather
    /// than being deleted. Removing a name says the name was wrong, not that the detections were:
    /// the same faces are immediately available to name correctly, without the library being
    /// processed again.
    /// </remarks>
    public async Task<PersonActionResult> DeleteAsync(
        PersonId personId, string detectorId, CancellationToken ct)
    {
        var person = await people.GetByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return PersonActionResult.Failed("That person no longer exists.");
        }

        var assigned = await faces.GetByPersonAsync(personId, detectorId, ct).ConfigureAwait(false);

        // Faces a user explicitly rejected keep that decision. Their person is going away, but the
        // statement "this is not that person" was still made and should survive.
        await faces.AssignAsync(
            [.. assigned
                .Where(face => face.Assignment != Assignment.Rejected)
                .Select(face => new FaceAssignment(face.Id, null, Assignment.Auto))],
            ct).ConfigureAwait(false);

        await clusters.ClearPersonAsync(personId, ct).ConfigureAwait(false);
        await people.RemoveAsync(personId, ct).ConfigureAwait(false);

        return PersonActionResult.Succeeded(
            $"Removed {person.Name}. {assigned.Count:N0} photo(s) are available to name again.");
    }

    /// <summary>
    /// Takes photographs off a person because that face is not them.
    /// </summary>
    /// <remarks>
    /// Recorded as a rejection rather than simply cleared. A cleared face is indistinguishable
    /// from one never grouped, so the next grouping would put it straight back and the correction
    /// would have to be made again after every run. A rejection is a decision the automatic pass
    /// is not permitted to overrule.
    ///
    /// The average is recomputed afterwards, which it was not until this took a detector and an
    /// embedder to do it with. Without that, taking photographs off somebody left them carrying an
    /// average of faces they no longer had, and that average is what the next grouping run compares
    /// new faces against — so the removal quietly went on describing them.
    /// </remarks>
    public async Task<PersonActionResult> RemoveFacesAsync(
        PersonId personId,
        IReadOnlyList<FaceId> faceIds,
        string detectorId,
        string embedderId,
        CancellationToken ct)
    {
        if (faceIds.Count == 0)
        {
            return PersonActionResult.Failed("Nothing was selected.");
        }

        var person = await people.GetByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return PersonActionResult.Failed("That person no longer exists.");
        }

        await faces.AssignAsync(
            [.. faceIds.Select(id => new FaceAssignment(id, null, Assignment.Rejected))],
            ct).ConfigureAwait(false);

        await UpdateCentroidAsync(person, detectorId, embedderId, ct).ConfigureAwait(false);

        return PersonActionResult.Succeeded(
            $"Removed {faceIds.Count:N0} photo(s) from {person.Name}. They will not be added back automatically.");
    }
}

/// <param name="Box">
/// Where this face sits in the photograph. Carried so the review grid can show the face as well as
/// the picture: a person with two faces in one photograph, or one photograph holding two people,
/// otherwise produces tiles that are pixel-identical and impossible to tell apart.
/// </param>
/// <param name="Assignment">Whether this face was grouped automatically or decided by the user.</param>
public readonly record struct PersonPhoto(
    FaceId FaceId, PhotoId PhotoId, string Path, FaceBox Box, Assignment Assignment);

/// <summary>A person, reduced to what a picker needs.</summary>
public readonly record struct PersonSummary(PersonId Id, string Name)
{
    public override string ToString() => Name;
}

public readonly record struct PersonActionResult(bool IsSuccess, string Message)
{
    public static PersonActionResult Succeeded(string message) => new(true, message);

    public static PersonActionResult Failed(string message) => new(false, message);
}
