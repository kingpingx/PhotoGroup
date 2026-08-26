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
                results.Add(new PersonPhoto(face.Id, photo.Id, photo.Path, face.Assignment));
            }
        }

        return results;
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
    /// </remarks>
    public async Task<PersonActionResult> RemoveFacesAsync(
        PersonId personId, IReadOnlyList<FaceId> faceIds, CancellationToken ct)
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

        return PersonActionResult.Succeeded(
            $"Removed {faceIds.Count:N0} photo(s) from {person.Name}. They will not be added back automatically.");
    }
}

/// <param name="Assignment">Whether this face was grouped automatically or decided by the user.</param>
public readonly record struct PersonPhoto(FaceId FaceId, PhotoId PhotoId, string Path, Assignment Assignment);

public readonly record struct PersonActionResult(bool IsSuccess, string Message)
{
    public static PersonActionResult Succeeded(string message) => new(true, message);

    public static PersonActionResult Failed(string message) => new(false, message);
}
