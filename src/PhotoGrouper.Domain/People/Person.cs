using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.People;

/// <summary>
/// A named identity the user created.
/// </summary>
/// <remarks>
/// The one thing in the library no algorithm can regenerate. Detections, embeddings and clusters
/// are all derived from pixels and can be rebuilt at the cost of time; the fact that a particular
/// cluster of faces is called Alice exists only because somebody said so.
/// </remarks>
public sealed class Person
{
    public Person(
        PersonId id,
        PersonName name,
        DateTimeOffset createdUtc,
        FaceId? coverFaceId = null,
        float[]? centroid = null)
    {
        Id = id;
        Name = name;
        CreatedUtc = createdUtc;
        CoverFaceId = coverFaceId;
        Centroid = centroid;
    }

    public PersonId Id { get; }

    public PersonName Name { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>The face shown on this person's tile.</summary>
    public FaceId? CoverFaceId { get; private set; }

    /// <summary>
    /// Mean of this person's face embeddings.
    /// </summary>
    /// <remarks>
    /// A cache, not a source of truth: it is derived from the member faces and can be recomputed
    /// whenever they change. Kept because incremental assignment compares each newly scanned face
    /// against a few hundred of these rather than against every face in the library, which is what
    /// makes searching feel instant.
    /// </remarks>
    public float[]? Centroid { get; private set; }

    public void Rename(PersonName name) => Name = name;

    /// <summary>
    /// Sets, or with null clears, the face shown on this person's tile.
    /// </summary>
    /// <remarks>
    /// Nullable because a person can be emptied of faces, and a cover pointing at a face they no
    /// longer hold is worse than none: the column carries no foreign key, so nothing downstream
    /// would ever notice.
    /// </remarks>
    public void SetCoverFace(FaceId? faceId) => CoverFaceId = faceId;

    public void UpdateCentroid(float[]? centroid) => Centroid = centroid;
}
