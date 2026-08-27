using FluentAssertions;
using OpenCvSharp;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Infrastructure.Imaging;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers cutting one face out of a photograph for display.
/// </summary>
/// <remarks>
/// This exists because a face crop is the one image in the application whose correctness cannot be
/// judged by looking at it. A whole-photo thumbnail that comes out sideways or mirrored is obvious
/// on sight; a crop taken from the wrong part of the frame is a perfectly ordinary-looking picture
/// of somebody's shoulder, or of the person standing next to the one being asked about. It is
/// shown precisely so the user can tell two similar tiles apart, so a crop that silently drifts is
/// worse than no crop at all: it answers the question confidently and wrongly.
///
/// The fixtures are asymmetric on both axes — red left, blue right, a green marker in one corner —
/// so a crop of the left half and a crop of the right half are distinguishable, and a rotation, a
/// mirror or a transpose each produce a different and detectable result.
/// </remarks>
public sealed class FaceThumbnailTests : IDisposable
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "photogrouper-facecrops-" + Guid.NewGuid().ToString("N"));

    private readonly IImageDecoder _decoder = CompositeImageDecoder.CreateDefault();

    private DiskThumbnailCache Subject => new(_root, _decoder);

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    /// <summary>
    /// A face in the middle of the left, red half of the fixture.
    /// </summary>
    /// <remarks>
    /// Placed so that the crop stays inside one colour even after the portrait margin is added, and
    /// clear of the green corner marker. A box that straddles the boundary would pass whether the
    /// margin were applied or not, which is one of the things being checked.
    /// </remarks>
    private static readonly FaceBox LeftFace = new(6, 6, 8, 8, 0.9f);

    /// <summary>The same, in the right, blue half.</summary>
    private static readonly FaceBox RightFace = new(26, 6, 8, 8, 0.9f);

    /// <summary>Reads a pixel as red, green, blue from a BGR buffer.</summary>
    private static (byte R, byte G, byte B) PixelAt(ImageBuffer buffer, int x, int y)
    {
        var offset = (y * buffer.Stride) + (x * 3);
        var pixels = buffer.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>The colour at the middle of a stored crop, which is the face itself.</summary>
    private async Task<(byte R, byte G, byte B)> CentreOfCropAsync(string path)
    {
        var decoded = await _decoder.DecodeAsync(path, null, default);
        decoded.Should().NotBeNull("the crop that was just written must be readable");

        var buffer = decoded!.Buffer;
        return PixelAt(buffer, buffer.Width / 2, buffer.Height / 2);
    }

    private static void ShouldBeRoughly(
        (byte R, byte G, byte B) actual, (byte R, byte G, byte B) expected, string because)
    {
        // JPEG is lossy and these are saturated colours, so an exact match is not available. A
        // generous tolerance still distinguishes red from blue from green.
        actual.R.Should().BeCloseTo(expected.R, 60, because);
        actual.G.Should().BeCloseTo(expected.G, 60, because);
        actual.B.Should().BeCloseTo(expected.B, 60, because);
    }

    [Fact]
    public async Task A_crop_comes_from_where_the_box_says_it_does()
    {
        var subject = Subject;

        var left = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), LeftFace, default);
        var right = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), RightFace, default);

        ShouldBeRoughly(await CentreOfCropAsync(left!), (255, 0, 0), "that box sits in the red half");
        ShouldBeRoughly(await CentreOfCropAsync(right!), (0, 0, 255), "that box sits in the blue half");
    }

    /// <remarks>
    /// The failure this rules out is the one that costs most: face boxes are stored against the
    /// upright image, so a crop taken from the stored pixels of a rotated photograph lands ninety
    /// degrees away from the face. Every phone photograph in portrait is affected, and the result
    /// still looks like a photograph, so nothing about it reads as a bug.
    /// </remarks>
    [Theory]
    [InlineData("upright.jpg")]
    [InlineData("orientation3.jpg")]
    [InlineData("orientation6.jpg")]
    [InlineData("orientation8.jpg")]
    public async Task A_crop_is_taken_from_the_upright_image_whatever_the_file_says(string fixture)
    {
        var subject = Subject;

        var left = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture(fixture), LeftFace, default);
        var right = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture(fixture), RightFace, default);

        ShouldBeRoughly(await CentreOfCropAsync(left!), (255, 0, 0), "the left of the upright image is red");
        ShouldBeRoughly(await CentreOfCropAsync(right!), (0, 0, 255), "the right of the upright image is blue");
    }

    /// <remarks>
    /// One photograph yields as many crops as it has faces, which is the entire reason this is
    /// keyed by face. Keying it by photograph would make every face in a group shot overwrite the
    /// last, and the tiles asking about three different people would all show the same one.
    /// </remarks>
    [Fact]
    public async Task Two_faces_in_one_photograph_are_stored_separately()
    {
        var subject = Subject;

        var left = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), LeftFace, default);
        var right = await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), RightFace, default);

        right.Should().NotBe(left);
        ShouldBeRoughly(await CentreOfCropAsync(left!), (255, 0, 0), "each face keeps its own crop");
        ShouldBeRoughly(await CentreOfCropAsync(right!), (0, 0, 255), "each face keeps its own crop");
    }

    [Fact]
    public async Task The_box_is_grown_before_cropping()
    {
        // The detector's box stops at the chin and the hairline, which makes a poor portrait. The
        // stored crop is expected to be larger than the box it came from.
        var path = await Subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), LeftFace, default);

        var decoded = await _decoder.DecodeAsync(path!, null, default);

        decoded!.Buffer.Width.Should().BeGreaterThan((int)LeftFace.Width);
        decoded.Buffer.Height.Should().BeGreaterThan((int)LeftFace.Height);
    }

    [Fact]
    public async Task A_second_request_reuses_the_stored_crop()
    {
        var subject = Subject;
        var faceId = FaceId.New();

        var first = await subject.GetOrCreateFaceAsync(faceId, Fixture("upright.jpg"), LeftFace, default);
        var second = await subject.GetOrCreateFaceAsync(faceId, Fixture("upright.jpg"), LeftFace, default);

        second.Should().Be(first);
        File.Exists(first!).Should().BeTrue();
    }

    /// <remarks>
    /// Stored coordinates and a photograph can stop describing each other, if the file is replaced
    /// after detection ran. Nothing sensible can be cropped then, and cropping anyway would put a
    /// confident picture of the wrong thing on somebody's tile.
    /// </remarks>
    [Fact]
    public async Task A_box_outside_the_photograph_yields_nothing()
    {
        var offImage = new FaceBox(500, 500, 40, 40, 0.9f);

        var path = await Subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), offImage, default);

        path.Should().BeNull();
    }

    [Fact]
    public async Task An_unreadable_source_yields_nothing()
    {
        var path = await Subject.GetOrCreateFaceAsync(
            FaceId.New(), Fixture("does-not-exist.jpg"), LeftFace, default);

        path.Should().BeNull();
    }

    /// <remarks>
    /// The bound is what keeps a face cache from costing more disk than the thumbnails it sits
    /// beside: a face filling a twelve megapixel frame would otherwise be stored at close to that
    /// size to be displayed at sixty pixels.
    /// </remarks>
    [Fact]
    public async Task A_large_crop_is_reduced_to_the_stored_size()
    {
        var large = Path.Combine(_root, "large.jpg");
        Directory.CreateDirectory(_root);
        using (var canvas = new Mat(1200, 2000, MatType.CV_8UC3, Scalar.Red))
        {
            Cv2.ImWrite(large, canvas);
        }

        var wholeFrame = new FaceBox(200, 100, 1000, 1000, 0.9f);
        var path = await Subject.GetOrCreateFaceAsync(FaceId.New(), large, wholeFrame, default);

        var decoded = await _decoder.DecodeAsync(path!, null, default);

        Math.Max(decoded!.Buffer.Width, decoded.Buffer.Height)
            .Should().Be(DiskThumbnailCache.FaceCropLongEdge);
    }

    /// <remarks>
    /// Face crops live under the thumbnail root so that clearing the cache and reporting its size
    /// cover both without either having to be taught that face crops exist.
    /// </remarks>
    [Fact]
    public async Task Crops_are_counted_and_cleared_with_the_rest_of_the_cache()
    {
        var subject = Subject;
        await subject.GetOrCreateFaceAsync(FaceId.New(), Fixture("upright.jpg"), LeftFace, default);

        subject.GetCacheSizeBytes().Should().BeGreaterThan(0);

        subject.Clear();

        subject.GetCacheSizeBytes().Should().Be(0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
