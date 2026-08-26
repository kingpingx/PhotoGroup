using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.Faces;

/// <summary>
/// One face found in one photo by one detector.
/// </summary>
/// <remarks>
/// A photo has zero or more of these, which is what makes "who is in this picture" a question
/// the app can answer at all. Everything the user cares about — names, confirmations, the
/// grouping itself — hangs off this row.
///
/// Faces are scoped to the detector that found them, and both detectors' faces coexist for the
/// same photo. That costs a little space and buys reversibility: switching detectors
/// deactivates the previous set rather than deleting it, so switching back restores every
/// person assignment immediately instead of requiring the library to be processed again.
/// </remarks>
public sealed class Face
{
    public Face(
        FaceId id,
        PhotoId photoId,
        string detectorId,
        string detectorVersion,
        FaceBox box,
        FaceLandmarks landmarks,
        bool isActive = true,
        float? blurScore = null,
        PersonId? personId = null,
        ClusterId? clusterId = null,
        Assignment assignment = Assignment.Auto)
    {
        if (string.IsNullOrWhiteSpace(detectorId))
        {
            throw new ArgumentException("A face must record which detector found it.", nameof(detectorId));
        }

        Id = id;
        PhotoId = photoId;
        DetectorId = detectorId;
        DetectorVersion = detectorVersion;
        Box = box;
        Landmarks = landmarks;
        IsActive = isActive;
        BlurScore = blurScore;
        PersonId = personId;
        ClusterId = clusterId;
        Assignment = assignment;
    }

    public FaceId Id { get; }

    public PhotoId PhotoId { get; }

    /// <summary>Which detector produced this face. Every face query filters on it.</summary>
    public string DetectorId { get; }

    /// <summary>
    /// Bumped when a detector's behaviour changes enough to invalidate its stored output.
    /// </summary>
    public string DetectorVersion { get; }

    /// <summary>
    /// False once a different detector has become the active one.
    /// </summary>
    /// <remarks>
    /// Retained rather than deleted so that switching detectors is reversible. A user who tries
    /// the other detector and dislikes the result gets their previous state back instantly.
    /// </remarks>
    public bool IsActive { get; private set; }

    public FaceBox Box { get; }

    public FaceLandmarks Landmarks { get; }

    /// <summary>Variance of the Laplacian over the face crop; higher is sharper.</summary>
    public float? BlurScore { get; }

    public PersonId? PersonId { get; private set; }

    public ClusterId? ClusterId { get; private set; }

    public Assignment Assignment { get; private set; }

    /// <summary>The size used for quality gating and for choosing a cover face.</summary>
    public int FacePixels => (int)MathF.Round(Box.SmallestSide);

    /// <summary>True when the user has expressed an opinion that clustering must not overrule.</summary>
    public bool IsUserDecided => Assignment is Assignment.Confirmed or Assignment.Rejected;

    public void AssignTo(PersonId person, Assignment assignment)
    {
        if (IsUserDecided && assignment == Assignment.Auto)
        {
            throw new InvalidOperationException(
                "An automatic assignment cannot overwrite a decision the user made by hand.");
        }

        PersonId = person;
        Assignment = assignment;
    }

    public void Unassign()
    {
        PersonId = null;
        Assignment = Assignment.Auto;
    }

    public void PlaceInCluster(ClusterId cluster) => ClusterId = cluster;

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
