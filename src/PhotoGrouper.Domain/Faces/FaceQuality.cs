namespace PhotoGrouper.Domain.Faces;

/// <summary>
/// The bar a detection must clear to be worth embedding.
/// </summary>
/// <remarks>
/// Junk detections are the main cause of bad clusters. A twenty pixel face in the background
/// of a crowd carries almost no identity signal, but it still produces a 512-float vector that
/// clustering will dutifully try to group with something. Discarding these early costs a few
/// real faces at the margin and saves a great deal of nonsense downstream.
///
/// Expressed as a domain rule rather than a detector setting so that the bar is the same
/// whichever detector is active, and so changing it is a single decision in one place.
/// </remarks>
public sealed record FaceQuality(float MinimumScore, int MinimumFacePixels, float MinimumBlurScore)
{
    /// <summary>
    /// The default gate.
    /// </summary>
    /// <remarks>
    /// Forty pixels is roughly where recognition accuracy begins to fall away sharply: the
    /// embedder resizes to 112 pixels square, so anything much smaller is being upscaled from
    /// too little information to distinguish one person from another.
    /// </remarks>
    public static readonly FaceQuality Default = new(MinimumScore: 0.6f, MinimumFacePixels: 40, MinimumBlurScore: 0f);

    public bool IsAcceptable(FaceBox box, float? blurScore = null) =>
        box.Score >= MinimumScore
        && box.SmallestSide >= MinimumFacePixels
        && (blurScore is null || blurScore >= MinimumBlurScore);

    /// <summary>Explains why a detection was discarded, for the diagnostics view.</summary>
    public string? DescribeRejection(FaceBox box, float? blurScore = null)
    {
        if (box.Score < MinimumScore)
        {
            return $"confidence {box.Score:F2} below {MinimumScore:F2}";
        }

        if (box.SmallestSide < MinimumFacePixels)
        {
            return $"face {box.SmallestSide:F0}px below {MinimumFacePixels}px";
        }

        if (blurScore is { } blur && blur < MinimumBlurScore)
        {
            return $"blur {blur:F1} below {MinimumBlurScore:F1}";
        }

        return null;
    }
}
