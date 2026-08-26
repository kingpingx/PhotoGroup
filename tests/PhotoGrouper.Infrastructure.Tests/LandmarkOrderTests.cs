using FluentAssertions;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Pins each detector's landmark reordering.
/// </summary>
/// <remarks>
/// Both models emit the subject's right eye before their left. The subject's right eye is on the
/// viewer's left, which is where the application's LeftEye belongs — the two conventions name
/// the same point from opposite viewpoints, and it is very easy to carry the model's order
/// through unchanged.
///
/// Doing so mirrors every alignment. The crop still looks like a face, the embedder still
/// returns a well-formed vector, and clustering still runs; it simply groups people wrongly, with
/// no error anywhere to suggest why. Synthetic input is used deliberately: the mapping can be
/// verified exactly, and no photograph of a real person needs to be committed to do it.
/// </remarks>
public sealed class LandmarkOrderTests
{
    /// <summary>
    /// A synthetic model row for an upright face.
    /// </summary>
    /// <remarks>
    /// Laid out as the models emit it: subject's right eye, subject's left eye, nose, subject's
    /// right mouth corner, subject's left mouth corner. The subject's right side has the smaller
    /// x, because they are facing the viewer.
    /// </remarks>
    private static readonly float[] ModelOrder =
    [
        30, 40,   // subject's right eye  -> viewer's left
        70, 40,   // subject's left eye   -> viewer's right
        50, 60,   // nose
        35, 80,   // subject's right mouth corner
        65, 80,   // subject's left mouth corner
    ];

    [Fact]
    public void YuNet_maps_the_subjects_right_eye_to_the_viewers_left()
    {
        var landmarks = YuNetDetector.ReadLandmarks(ModelOrder);

        landmarks.LeftEye.X.Should().Be(30);
        landmarks.RightEye.X.Should().Be(70);
    }

    [Fact]
    public void YuNet_produces_a_plausibly_ordered_face() =>
        YuNetDetector.ReadLandmarks(ModelOrder).IsPlausiblyOrdered().Should().BeTrue();

    [Fact]
    public void YuNet_places_the_nose_and_mouth_correctly()
    {
        var landmarks = YuNetDetector.ReadLandmarks(ModelOrder);

        landmarks.Nose.Should().Be(new PhotoGrouper.Domain.Common.Point2(50, 60));
        landmarks.MouthLeft.X.Should().BeLessThan(landmarks.MouthRight.X);
    }

    [Fact]
    public void Scrfd_shares_the_same_convention()
    {
        // SCRFD's landmarks arrive as offsets from an anchor centre rather than absolute points,
        // so its mapping runs through the shared decoder. Verified here against the same layout
        // so both detectors are pinned by the same expectation.
        var offsets = ModelOrder.Select(v => v - 50f).ToArray();

        var landmarks = DetectionMath.DistanceToLandmarks(new PhotoGrouper.Domain.Common.Point2(50, 50), offsets);

        landmarks.LeftEye.X.Should().BeLessThan(landmarks.RightEye.X);
        landmarks.IsPlausiblyOrdered().Should().BeTrue();
    }
}
