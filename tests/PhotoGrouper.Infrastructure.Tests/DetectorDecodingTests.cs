using FluentAssertions;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers the arithmetic that turns raw model output into face coordinates.
/// </summary>
/// <remarks>
/// This is where detection goes wrong quietly. Every mistake available here — a forgotten stride
/// multiplication, an unmapped letterbox, a transposed anchor grid, a permuted landmark order —
/// produces boxes that are well-formed and plausible-looking but in the wrong place or of the
/// wrong size. None of them throws. Tested with synthetic values so the expected answers can be
/// worked out by hand, and without needing a photograph of a real person as a fixture.
/// </remarks>
public sealed class LetterboxTests
{
    [Fact]
    public void A_square_image_fills_the_input_with_no_padding()
    {
        var box = Letterbox.Fit(1000, 1000, 640);

        box.Scale.Should().BeApproximately(0.64f, 0.0001f);
        box.PadX.Should().Be(0);
        box.PadY.Should().Be(0);
    }

    [Fact]
    public void A_wide_image_is_padded_below()
    {
        // 1280x720 scaled by 0.5 gives 640x360, leaving 280 rows of padding.
        var box = Letterbox.Fit(1280, 720, 640);

        box.Scale.Should().BeApproximately(0.5f, 0.0001f);
        box.ScaledWidth.Should().Be(640);
        box.ScaledHeight.Should().Be(360);
        box.PadX.Should().Be(0);
        box.PadY.Should().Be(280);
    }

    [Fact]
    public void A_tall_image_is_padded_to_the_right()
    {
        var box = Letterbox.Fit(720, 1280, 640);

        box.ScaledHeight.Should().Be(640);
        box.PadX.Should().Be(280);
        box.PadY.Should().Be(0);
    }

    [Fact]
    public void Aspect_ratio_is_preserved()
    {
        // The reason for padding at all: a stretched face is not a face any detector was
        // trained on, and recall falls sharply when the shape is wrong.
        var box = Letterbox.Fit(1600, 400, 640);

        var sourceAspect = 1600f / 400f;
        var fittedAspect = (float)box.ScaledWidth / box.ScaledHeight;

        fittedAspect.Should().BeApproximately(sourceAspect, 0.02f);
    }

    [Fact]
    public void A_box_maps_back_to_source_coordinates()
    {
        var letterbox = Letterbox.Fit(1280, 720, 640);

        var mapped = letterbox.ToSource(new FaceBox(100, 50, 40, 60, 0.9f));

        mapped.X.Should().BeApproximately(200, 0.001f);
        mapped.Y.Should().BeApproximately(100, 0.001f);
        mapped.Width.Should().BeApproximately(80, 0.001f);
        mapped.Height.Should().BeApproximately(120, 0.001f);
    }

    [Fact]
    public void The_score_survives_the_mapping() =>
        Letterbox.Fit(1280, 720, 640).ToSource(new FaceBox(10, 10, 10, 10, 0.87f))
            .Score.Should().BeApproximately(0.87f, 0.0001f);

    [Fact]
    public void A_box_running_past_the_edge_is_clamped_to_the_image()
    {
        // Detectors routinely place part of a box outside the frame for a face that is partly
        // out of shot. Clamping keeps every stored box usable as a crop.
        var letterbox = Letterbox.Fit(100, 100, 640);

        var mapped = letterbox.ToSource(new FaceBox(-50, -50, 200, 200, 0.9f));

        mapped.X.Should().Be(0);
        mapped.Y.Should().Be(0);
        mapped.Right.Should().BeLessThanOrEqualTo(100);
        mapped.Bottom.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Landmarks_are_not_clamped()
    {
        // Unlike boxes. A landmark just outside the frame is a real estimate of where an
        // occluded eye lies, and alignment wants it; pinning it to the edge skews the transform.
        var letterbox = Letterbox.Fit(100, 100, 640);
        var outside = new FaceLandmarks(
            new Point2(-32, -32), new Point2(0, 0), new Point2(0, 0), new Point2(0, 0), new Point2(0, 0));

        letterbox.ToSource(outside).LeftEye.X.Should().BeLessThan(0);
    }

    [Fact]
    public void Rejects_a_source_with_no_area()
    {
        var act = () => Letterbox.Fit(0, 100, 640);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class DetectionMathTests
{
    [Fact]
    public void Anchor_centres_cover_the_input_in_row_major_order()
    {
        var centres = DetectionMath.BuildAnchorCentres(inputSize: 64, stride: 32, anchorsPerCell: 1);

        centres.Should().Equal(
            new Point2(0, 0), new Point2(32, 0),
            new Point2(0, 32), new Point2(32, 32));
    }

    [Fact]
    public void Multiple_anchors_per_cell_share_a_centre()
    {
        // SCRFD predicts two boxes at every grid position. Flattening them in the wrong order
        // pairs each prediction with the wrong anchor and scatters boxes across the image.
        var centres = DetectionMath.BuildAnchorCentres(inputSize: 64, stride: 32, anchorsPerCell: 2);

        centres.Should().HaveCount(8);
        centres[0].Should().Be(centres[1]);
        centres[2].Should().Be(centres[3]);
        centres[2].Should().Be(new Point2(32, 0));
    }

    [Fact]
    public void The_anchor_count_matches_what_the_model_declares()
    {
        // SCRFD at 640 declares 12800, 3200 and 800 rows for strides 8, 16 and 32. Agreeing with
        // those numbers is what confirms the grid is being built the way the model flattened it.
        DetectionMath.BuildAnchorCentres(640, 8, 2).Should().HaveCount(12800);
        DetectionMath.BuildAnchorCentres(640, 16, 2).Should().HaveCount(3200);
        DetectionMath.BuildAnchorCentres(640, 32, 2).Should().HaveCount(800);
    }

    [Fact]
    public void Distances_from_a_centre_become_a_box()
    {
        var box = DetectionMath.DistanceToBox(new Point2(100, 100), left: 10, top: 20, right: 30, bottom: 40, score: 0.5f);

        box.X.Should().Be(90);
        box.Y.Should().Be(80);
        box.Width.Should().Be(40);
        box.Height.Should().Be(60);
    }

    [Fact]
    public void Landmark_offsets_are_added_to_the_centre()
    {
        float[] offsets = [-10, -5, 10, -5, 0, 0, -8, 10, 8, 10];

        var landmarks = DetectionMath.DistanceToLandmarks(new Point2(50, 50), offsets);

        landmarks.LeftEye.Should().Be(new Point2(40, 45));
        landmarks.RightEye.Should().Be(new Point2(60, 45));
        landmarks.Nose.Should().Be(new Point2(50, 50));
        landmarks.IsPlausiblyOrdered().Should().BeTrue();
    }

    [Fact]
    public void Suppression_keeps_the_strongest_of_a_cluster_of_boxes()
    {
        // A detector fires on every anchor near a face. Without this one person becomes a dozen
        // faces, each embedded and clustered separately, filling the People page with duplicates.
        List<FaceBox> boxes =
        [
            new(100, 100, 50, 50, 0.90f),
            new(102, 101, 50, 50, 0.95f),
            new(98, 99, 50, 50, 0.85f),
        ];

        var kept = DetectionMath.NonMaximumSuppression(boxes, overlapThreshold: 0.4f);

        kept.Should().ContainSingle();
        boxes[kept[0]].Score.Should().Be(0.95f);
    }

    [Fact]
    public void Suppression_keeps_genuinely_separate_faces()
    {
        List<FaceBox> boxes =
        [
            new(0, 0, 50, 50, 0.9f),
            new(500, 500, 50, 50, 0.8f),
        ];

        DetectionMath.NonMaximumSuppression(boxes, 0.4f).Should().HaveCount(2);
    }

    [Fact]
    public void Suppression_of_nothing_yields_nothing() =>
        DetectionMath.NonMaximumSuppression([], 0.4f).Should().BeEmpty();

    [Fact]
    public void Results_are_ordered_by_confidence()
    {
        List<FaceBox> boxes =
        [
            new(0, 0, 50, 50, 0.5f),
            new(500, 0, 50, 50, 0.9f),
            new(1000, 0, 50, 50, 0.7f),
        ];

        var kept = DetectionMath.NonMaximumSuppression(boxes, 0.4f);

        kept.Select(i => boxes[i].Score).Should().BeInDescendingOrder();
    }
}
