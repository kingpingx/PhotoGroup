using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Domain.Faces;

/// <summary>
/// The five facial reference points, in a single canonical order.
/// </summary>
/// <remarks>
/// The order is left eye, right eye, nose, left mouth corner, right mouth corner, matching the
/// ArcFace alignment template. Detectors emit these in whatever order their author chose, so
/// every detector adapter is responsible for reordering into this one before returning.
///
/// This matters more than it looks. Alignment computes a transform from these points onto a
/// fixed template; feeding it a permuted order produces a mirrored or rotated crop that still
/// looks like a face, still embeds without error, and still yields a 512-float vector. Nothing
/// fails. The embeddings are simply wrong, and clustering quietly falls apart with no
/// indication of why. Normalising at the adapter boundary is what keeps that impossible.
///
/// "Left" means the viewer's left, which is the subject's right. This is the convention the
/// reference template uses and reversing it produces exactly the mirrored-alignment defect
/// described above.
/// </remarks>
public readonly record struct FaceLandmarks(
    Point2 LeftEye,
    Point2 RightEye,
    Point2 Nose,
    Point2 MouthLeft,
    Point2 MouthRight)
{
    /// <summary>Number of floats in the serialised form: five points of two coordinates.</summary>
    public const int ValueCount = 10;

    /// <summary>The points in canonical order.</summary>
    public Point2[] ToArray() => [LeftEye, RightEye, Nose, MouthLeft, MouthRight];

    /// <summary>Flattens to x0, y0, x1, y1, … for storage.</summary>
    public float[] ToFloats() =>
    [
        LeftEye.X, LeftEye.Y,
        RightEye.X, RightEye.Y,
        Nose.X, Nose.Y,
        MouthLeft.X, MouthLeft.Y,
        MouthRight.X, MouthRight.Y,
    ];

    public static FaceLandmarks FromFloats(ReadOnlySpan<float> values)
    {
        if (values.Length != ValueCount)
        {
            throw new ArgumentException(
                $"Expected {ValueCount} values but received {values.Length}.", nameof(values));
        }

        return new FaceLandmarks(
            new Point2(values[0], values[1]),
            new Point2(values[2], values[3]),
            new Point2(values[4], values[5]),
            new Point2(values[6], values[7]),
            new Point2(values[8], values[9]));
    }

    /// <summary>Distance between the eyes, a scale-free measure of how large the face is.</summary>
    public float InterocularDistance => LeftEye.DistanceTo(RightEye);

    /// <summary>Shifts every point by an offset, for mapping a crop back into full-image coordinates.</summary>
    public FaceLandmarks Translate(Point2 offset) => new(
        LeftEye + offset, RightEye + offset, Nose + offset, MouthLeft + offset, MouthRight + offset);

    /// <summary>Scales every point about the origin.</summary>
    public FaceLandmarks Scale(float factor) => new(
        LeftEye * factor, RightEye * factor, Nose * factor, MouthLeft * factor, MouthRight * factor);

    /// <summary>
    /// True when the points are arranged as a forward-facing face rather than a mirrored one.
    /// </summary>
    /// <remarks>
    /// A cheap sanity check on a detector adapter's ordering: for any upright face the left eye
    /// is to the left of the right eye and the mouth sits below both. If a detector's landmark
    /// order were transcribed wrongly, this is what would notice.
    /// </remarks>
    public bool IsPlausiblyOrdered() =>
        LeftEye.X < RightEye.X
        && MouthLeft.X < MouthRight.X
        && Nose.Y > MathF.Min(LeftEye.Y, RightEye.Y)
        && MathF.Min(MouthLeft.Y, MouthRight.Y) > MathF.Min(LeftEye.Y, RightEye.Y);
}
