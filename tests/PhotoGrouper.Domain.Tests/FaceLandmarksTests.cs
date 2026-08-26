using FluentAssertions;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Domain.Tests;

/// <summary>
/// Covers the canonical landmark order and its serialisation.
/// </summary>
/// <remarks>
/// These points feed a similarity transform onto a fixed template. A permuted order still
/// produces a transform, still produces a crop, and still produces a 512-float embedding; it
/// simply describes a mirrored or rotated face. Nothing raises an error, so ordering has to be
/// pinned by tests or it is not pinned at all.
/// </remarks>
public sealed class FaceLandmarksTests
{
    private static FaceLandmarks Upright() => new(
        LeftEye: new Point2(30, 40),
        RightEye: new Point2(70, 40),
        Nose: new Point2(50, 60),
        MouthLeft: new Point2(35, 80),
        MouthRight: new Point2(65, 80));

    [Fact]
    public void Serialises_in_canonical_order()
    {
        Upright().ToFloats().Should().Equal(30, 40, 70, 40, 50, 60, 35, 80, 65, 80);
    }

    [Fact]
    public void Round_trips_through_floats() =>
        FaceLandmarks.FromFloats(Upright().ToFloats()).Should().Be(Upright());

    [Fact]
    public void Rejects_the_wrong_number_of_values()
    {
        var act = () => FaceLandmarks.FromFloats(new float[8]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_upright_face_is_plausibly_ordered() =>
        Upright().IsPlausiblyOrdered().Should().BeTrue();

    [Fact]
    public void Swapping_the_eyes_is_detected_as_implausible()
    {
        // Exactly the defect the check exists to catch: a detector adapter transcribing its
        // model's "right eye first" convention without mapping it to the viewer's frame.
        var mirrored = Upright() with
        {
            LeftEye = Upright().RightEye,
            RightEye = Upright().LeftEye,
        };

        mirrored.IsPlausiblyOrdered().Should().BeFalse();
    }

    [Fact]
    public void A_face_with_the_mouth_above_the_eyes_is_implausible()
    {
        var upsideDown = Upright() with
        {
            MouthLeft = new Point2(35, 10),
            MouthRight = new Point2(65, 10),
        };

        upsideDown.IsPlausiblyOrdered().Should().BeFalse();
    }

    [Fact]
    public void Interocular_distance_is_the_gap_between_the_eyes() =>
        Upright().InterocularDistance.Should().BeApproximately(40f, 0.001f);

    [Fact]
    public void Scaling_maps_every_point_by_the_same_factor()
    {
        var scaled = Upright().Scale(2f);

        scaled.LeftEye.Should().Be(new Point2(60, 80));
        scaled.MouthRight.Should().Be(new Point2(130, 160));
    }

    [Fact]
    public void Translating_shifts_every_point() =>
        Upright().Translate(new Point2(10, -5)).Nose.Should().Be(new Point2(60, 55));
}
