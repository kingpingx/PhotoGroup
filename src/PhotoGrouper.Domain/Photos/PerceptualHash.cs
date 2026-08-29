namespace PhotoGrouper.Domain.Photos;

/// <summary>
/// A 128-bit fingerprint of what a photograph looks like.
/// </summary>
/// <remarks>
/// Not a content hash and not a substitute for one. A content hash answers "is this the same
/// file"; two bytes differ and the answer is no. This answers "is this the same picture", which is
/// the question a burst of camera shots poses: eight frames of one scene, seconds apart, differing
/// in a blink and a shift of the shoulders, sharing not one byte.
///
/// Comparison is by Hamming distance — how many of the bits differ. That is why the value is a
/// pair of fixed-width integers rather than a string: the whole point is that near values are
/// meaningful, and a hex string invites the equality comparison that would throw that away.
///
/// It carries two different readings of the image, because one is not enough. The usual difference
/// hash records only whether each point is brighter than the one to its right, which cannot tell a
/// gradient from a flat wall: both answer "no" everywhere. A picture and its own transposition come
/// out identical under it. Adding the same comparison in the other direction does not help, because
/// it is the same blindness turned ninety degrees.
///
/// So the second half records where the light actually is — which parts of the frame are brighter
/// than the frame's own average. That distinguishes the cases the first half cannot, while staying
/// indifferent to overall exposure, since shifting every pixel shifts the average with it.
/// </remarks>
/// <param name="Gradient">Where the image gets lighter and darker from point to point.</param>
/// <param name="Brightness">Which parts of the frame are lighter than the frame as a whole.</param>
public readonly record struct PerceptualHash(ulong Gradient, ulong Brightness)
{
    /// <summary>How many bits a fingerprint carries, which is the largest distance possible.</summary>
    public const int Bits = 128;

    /// <summary>How many of the bits differ from another fingerprint.</summary>
    /// <remarks>
    /// Zero means the two reduce to an identical picture. Small numbers mean the same scene; large
    /// numbers mean different pictures. Where the line falls is a judgement the caller makes, not
    /// a property of the fingerprint, so it is not encoded here.
    /// </remarks>
    public int DistanceTo(PerceptualHash other) =>
        System.Numerics.BitOperations.PopCount(Gradient ^ other.Gradient)
        + System.Numerics.BitOperations.PopCount(Brightness ^ other.Brightness);

    public override string ToString() => $"{Gradient:X16}{Brightness:X16}";
}
