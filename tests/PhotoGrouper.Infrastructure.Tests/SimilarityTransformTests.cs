using FluentAssertions;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Infrastructure.Vision;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers the transform that squares a face up for the embedder.
/// </summary>
/// <remarks>
/// Worth pinning precisely because its failures are invisible. A transform that is wrong by a
/// rotation, a scale factor, or a mirror still produces a 112 pixel crop that looks like a face,
/// still embeds without complaint, and still yields 512 well-formed floats. The only symptom is
/// that people stop grouping, with nothing anywhere to say why.
///
/// The mirror case gets the most attention. The general form of Umeyama's method permits a
/// reflection when it fits the points better, and for a roughly symmetric face it frequently
/// does. This implementation excludes reflections by construction, and these tests are what say
/// so out loud.
/// </remarks>
public sealed class SimilarityTransformTests
{
    private static readonly Point2[] Square =
    [
        new(0, 0), new(10, 0), new(10, 10), new(0, 10),
    ];

    private static void ShouldMap(SimilarityTransform transform, Point2 from, Point2 to, float tolerance = 0.001f)
    {
        var mapped = transform.Apply(from);
        mapped.X.Should().BeApproximately(to.X, tolerance);
        mapped.Y.Should().BeApproximately(to.Y, tolerance);
    }

    [Fact]
    public void Identical_point_sets_yield_the_identity()
    {
        var transform = SimilarityTransform.Solve(Square, Square);

        transform.Scale.Should().BeApproximately(1f, 0.001f);
        transform.RotationRadians.Should().BeApproximately(0f, 0.001f);
        ShouldMap(transform, new Point2(5, 5), new Point2(5, 5));
    }

    [Fact]
    public void A_pure_translation_is_recovered()
    {
        Point2[] shifted = [.. Square.Select(p => new Point2(p.X + 7, p.Y - 3))];

        var transform = SimilarityTransform.Solve(Square, shifted);

        transform.Scale.Should().BeApproximately(1f, 0.001f);
        ShouldMap(transform, new Point2(0, 0), new Point2(7, -3));
    }

    [Fact]
    public void A_pure_scale_is_recovered()
    {
        Point2[] doubled = [.. Square.Select(p => new Point2(p.X * 2, p.Y * 2))];

        var transform = SimilarityTransform.Solve(Square, doubled);

        transform.Scale.Should().BeApproximately(2f, 0.001f);
        ShouldMap(transform, new Point2(5, 5), new Point2(10, 10));
    }

    [Fact]
    public void A_quarter_turn_is_recovered()
    {
        // Rotating (x, y) to (-y, x) is ninety degrees anticlockwise in a coordinate system whose
        // y axis points down, which is the convention image coordinates use.
        Point2[] rotated = [.. Square.Select(p => new Point2(-p.Y, p.X))];

        var transform = SimilarityTransform.Solve(Square, rotated);

        transform.Scale.Should().BeApproximately(1f, 0.001f);
        MathF.Abs(transform.RotationRadians).Should().BeApproximately(MathF.PI / 2, 0.001f);
        ShouldMap(transform, new Point2(10, 0), new Point2(0, 10));
    }

    [Fact]
    public void Rotation_and_scale_together_are_recovered()
    {
        Point2[] transformed = [.. Square.Select(p => new Point2(-p.Y * 3, p.X * 3))];

        var transform = SimilarityTransform.Solve(Square, transformed);

        transform.Scale.Should().BeApproximately(3f, 0.001f);
    }

    [Fact]
    public void A_mirrored_target_never_produces_a_reflection()
    {
        // The defect this class exists to prevent. A reflection shows up as a negative
        // determinant, so that is what must be impossible, whatever else the fit does.
        var spec = Application.Ports.AlignmentSpec.ArcFace112;
        Point2[] mirrored = [.. spec.ReferencePoints.Select(p => new Point2(112 - p.X, p.Y))];

        var transform = SimilarityTransform.Solve(spec.ReferencePoints, mirrored);

        Determinant(transform).Should().BeGreaterThanOrEqualTo(0,
            "excluding reflections is what stops a face being silently flipped left to right");
    }

    [Fact]
    public void Mapping_a_symmetric_shape_onto_its_mirror_collapses_rather_than_reflecting()
    {
        // A square mirrored about its own centre cannot be reached by any rotation and scale, so
        // the best reflection-free fit is the degenerate one that maps everything to a point.
        // Recorded deliberately: the resulting crop is useless, which is correct, and the
        // alternative of allowing a reflection to "succeed" here is exactly what must not happen.
        Point2[] mirrored = [.. Square.Select(p => new Point2(-p.X, p.Y))];

        var transform = SimilarityTransform.Solve(Square, mirrored);

        transform.Scale.Should().Be(0);
        Determinant(transform).Should().BeGreaterThanOrEqualTo(0);
    }

    private static float Determinant(SimilarityTransform t) =>
        (t.M11 * t.M22) - (t.M12 * t.M21);

    [Fact]
    public void The_matrix_form_agrees_with_applying_the_transform()
    {
        var transform = SimilarityTransform.Solve(
            Square, [.. Square.Select(p => new Point2((p.X * 2) + 5, (p.Y * 2) - 4))]);

        var matrix = transform.ToAffineMatrix();
        var point = new Point2(3, 7);

        var byMatrix = new Point2(
            (float)((matrix[0, 0] * point.X) + (matrix[0, 1] * point.Y) + matrix[0, 2]),
            (float)((matrix[1, 0] * point.X) + (matrix[1, 1] * point.Y) + matrix[1, 2]));

        ShouldMap(transform, point, byMatrix);
    }

    [Fact]
    public void The_matrix_is_two_rows_by_three_columns()
    {
        // OpenCV rejects anything else, and the failure surfaces deep inside a native call rather
        // than at the point the matrix is built.
        var matrix = SimilarityTransform.Identity.ToAffineMatrix();

        matrix.GetLength(0).Should().Be(2);
        matrix.GetLength(1).Should().Be(3);
    }

    [Fact]
    public void Landmarks_map_onto_the_ArcFace_template()
    {
        // The real case: five points from a detector, carried onto the reference positions the
        // model was trained against. A face twice the template's size and offset in the frame.
        var spec = Application.Ports.AlignmentSpec.ArcFace112;
        Point2[] detected = [.. spec.ReferencePoints.Select(p => new Point2((p.X * 2) + 300, (p.Y * 2) + 150))];

        var transform = SimilarityTransform.Solve(detected, spec.ReferencePoints);

        for (var i = 0; i < detected.Length; i++)
        {
            ShouldMap(transform, detected[i], spec.ReferencePoints[i], tolerance: 0.01f);
        }
    }

    [Fact]
    public void Imperfect_points_give_a_least_squares_fit_rather_than_failing()
    {
        // Real landmarks never sit exactly on the template; a face is not a rigid object. The
        // solution must be the best available compromise, not an exception.
        var spec = Application.Ports.AlignmentSpec.ArcFace112;
        var jitter = new Random(1);
        Point2[] noisy =
        [
            .. spec.ReferencePoints.Select(p =>
                new Point2(p.X + (float)((jitter.NextDouble() - 0.5) * 4), p.Y + (float)((jitter.NextDouble() - 0.5) * 4))),
        ];

        var transform = SimilarityTransform.Solve(noisy, spec.ReferencePoints);

        transform.Scale.Should().BeApproximately(1f, 0.15f);
    }

    [Fact]
    public void Collapsed_points_yield_the_identity_rather_than_dividing_by_zero()
    {
        // Happens for a very small or heavily occluded face, where a detector returns landmarks
        // all at nearly the same place. The resulting crop is useless but must not crash the run.
        Point2[] collapsed = [new(5, 5), new(5, 5), new(5, 5)];

        var transform = SimilarityTransform.Solve(collapsed, [new(1, 1), new(2, 2), new(3, 3)]);

        transform.Should().Be(SimilarityTransform.Identity);
    }

    [Fact]
    public void Mismatched_point_counts_are_rejected()
    {
        var act = () => SimilarityTransform.Solve(Square, [new Point2(0, 0)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fewer_than_two_points_are_rejected()
    {
        var act = () => SimilarityTransform.Solve([new Point2(0, 0)], [new Point2(1, 1)]);

        act.Should().Throw<ArgumentException>();
    }
}
