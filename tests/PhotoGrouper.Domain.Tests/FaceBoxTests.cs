using FluentAssertions;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Domain.Tests;

public sealed class FaceBoxTests
{
    [Fact]
    public void Identical_boxes_overlap_completely() =>
        new FaceBox(10, 10, 100, 100, 1f)
            .IntersectionOverUnion(new FaceBox(10, 10, 100, 100, 1f))
            .Should().BeApproximately(1f, 0.0001f);

    [Fact]
    public void Disjoint_boxes_do_not_overlap() =>
        new FaceBox(0, 0, 10, 10, 1f)
            .IntersectionOverUnion(new FaceBox(100, 100, 10, 10, 1f))
            .Should().Be(0f);

    [Fact]
    public void Boxes_sharing_only_an_edge_do_not_overlap() =>
        new FaceBox(0, 0, 10, 10, 1f)
            .IntersectionOverUnion(new FaceBox(10, 0, 10, 10, 1f))
            .Should().Be(0f);

    [Fact]
    public void Half_overlapping_boxes_score_one_third()
    {
        // Two 10x10 boxes offset by 5 on one axis: intersection 50, union 150.
        var overlap = new FaceBox(0, 0, 10, 10, 1f)
            .IntersectionOverUnion(new FaceBox(5, 0, 10, 10, 1f));

        overlap.Should().BeApproximately(1f / 3f, 0.0001f);
    }

    [Fact]
    public void Overlap_is_symmetric()
    {
        var a = new FaceBox(0, 0, 30, 20, 1f);
        var b = new FaceBox(10, 5, 30, 20, 1f);

        a.IntersectionOverUnion(b).Should().BeApproximately(b.IntersectionOverUnion(a), 0.0001f);
    }

    [Fact]
    public void The_smallest_side_is_what_gates_face_size() =>
        new FaceBox(0, 0, 200, 45, 1f).SmallestSide.Should().Be(45f);

    [Fact]
    public void Expanding_stays_inside_the_image()
    {
        var expanded = new FaceBox(5, 5, 20, 20, 1f).Expand(0.5f, imageWidth: 30, imageHeight: 30);

        expanded.X.Should().Be(0);
        expanded.Y.Should().Be(0);
        expanded.Right.Should().Be(30);
        expanded.Bottom.Should().Be(30);
    }
}
