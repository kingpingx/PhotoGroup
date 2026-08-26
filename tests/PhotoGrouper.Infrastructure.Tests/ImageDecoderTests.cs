using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Infrastructure.Imaging;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers decoding, including EXIF orientation.
/// </summary>
/// <remarks>
/// Orientation is the most consequential thing this layer does and the easiest to get wrong
/// without noticing. Cameras almost never rotate pixels; they store the sensor's landscape output
/// and record how it should be turned. A viewer that honours the tag makes this invisible, so a
/// pipeline that ignores it looks fine in the grid while handing every detector a sideways image,
/// finding far fewer faces, and placing the boxes it does find in coordinates that correspond to
/// nothing the user can see.
///
/// The fixtures are deliberately asymmetric on both axes — a wide image, red on the left, blue on
/// the right, with a green marker in one corner — so that a rotation, a mirror, or a transpose
/// each produce a different and detectable result. Checking only the dimensions would pass for a
/// mirrored image, which is exactly the failure that matters most.
/// </remarks>
public sealed class ImageDecoderTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly IImageDecoder _decoder = CompositeImageDecoder.CreateDefault();

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    /// <summary>Reads a pixel as red, green, blue from a BGR buffer.</summary>
    private static (byte R, byte G, byte B) PixelAt(ImageBuffer buffer, int x, int y)
    {
        var offset = (y * buffer.Stride) + (x * 3);
        var pixels = buffer.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static void ShouldBeRoughly(
        (byte R, byte G, byte B) actual, (byte R, byte G, byte B) expected, string because)
    {
        // JPEG is lossy and these are saturated colours at a block boundary, so an exact match is
        // not available. A generous tolerance still distinguishes red from blue from green.
        actual.R.Should().BeCloseTo(expected.R, 60, because);
        actual.G.Should().BeCloseTo(expected.G, 60, because);
        actual.B.Should().BeCloseTo(expected.B, 60, because);
    }

    [Theory]
    [InlineData("upright.jpg")]
    [InlineData("orientation3.jpg")]
    [InlineData("orientation6.jpg")]
    [InlineData("orientation8.jpg")]
    public async Task Every_orientation_decodes_to_the_same_upright_image(string fixture)
    {
        var decoded = await _decoder.DecodeAsync(Fixture(fixture), null, default);

        decoded.Should().NotBeNull();
        decoded!.Buffer.Width.Should().Be(40, "the upright image is wider than it is tall");
        decoded.Buffer.Height.Should().Be(20);
    }

    [Theory]
    [InlineData("upright.jpg")]
    [InlineData("orientation3.jpg")]
    [InlineData("orientation6.jpg")]
    [InlineData("orientation8.jpg")]
    public async Task Every_orientation_decodes_to_the_same_pixels(string fixture)
    {
        var decoded = await _decoder.DecodeAsync(Fixture(fixture), null, default);
        var buffer = decoded!.Buffer;

        // Sampled away from the edges, where JPEG blocking is worst.
        ShouldBeRoughly(PixelAt(buffer, 10, 10), (255, 0, 0), "the left half is red");
        ShouldBeRoughly(PixelAt(buffer, 30, 10), (0, 0, 255), "the right half is blue");
        ShouldBeRoughly(PixelAt(buffer, 1, 1), (0, 255, 0), "the marker sits in the top-left corner");
    }

    [Theory]
    [InlineData("orientation3.jpg", 3)]
    [InlineData("orientation6.jpg", 6)]
    [InlineData("orientation8.jpg", 8)]
    public async Task The_orientation_tag_is_reported(string fixture, int expected)
    {
        var metadata = await _decoder.ReadMetadataAsync(Fixture(fixture), default);

        metadata!.Orientation.Should().Be(expected);
    }

    [Fact]
    public async Task Reported_dimensions_are_of_the_upright_image()
    {
        // The file stores 20 by 40 and is tagged to be turned. Reporting the stored pair would put
        // every face box outside the photo's own declared bounds.
        var metadata = await _decoder.ReadMetadataAsync(Fixture("orientation6.jpg"), default);

        metadata!.Width.Should().Be(40);
        metadata.Height.Should().Be(20);
    }

    [Fact]
    public async Task An_image_with_no_exif_is_treated_as_upright() =>
        (await _decoder.ReadMetadataAsync(Fixture("upright.jpg"), default))!
            .Orientation.Should().Be(1);

    [Fact]
    public async Task HEIC_files_decode()
    {
        // The format modern iPhones capture in, and one OpenCV cannot read at all. Omitting it
        // would not fail loudly; a library imported from a phone would simply appear near-empty.
        var decoded = await _decoder.DecodeAsync(Fixture("sample.heic"), null, default);

        decoded.Should().NotBeNull();
        decoded!.Buffer.Width.Should().Be(40);
        decoded.Buffer.Height.Should().Be(20);
    }

    [Fact]
    public async Task HEIC_pixels_are_in_the_same_channel_order_as_JPEG()
    {
        // A different library decodes HEIC, so its channel order is an independent opportunity to
        // be wrong. Red and blue swapped would still produce a valid image and valid embeddings.
        var decoded = await _decoder.DecodeAsync(Fixture("sample.heic"), null, default);

        ShouldBeRoughly(PixelAt(decoded!.Buffer, 10, 10), (255, 0, 0), "the left half is red");
        ShouldBeRoughly(PixelAt(decoded.Buffer, 30, 10), (0, 0, 255), "the right half is blue");
    }

    [Fact]
    public async Task Decoding_respects_a_size_limit()
    {
        var decoded = await _decoder.DecodeAsync(Fixture("upright.jpg"), maxLongEdge: 20, default);

        decoded!.Buffer.Width.Should().Be(20);
        decoded.Scale.Should().BeApproximately(0.5f, 0.01f);
        decoded.OriginalWidth.Should().Be(40, "the caller needs the true size to scale coordinates back");
    }

    [Fact]
    public async Task A_small_image_is_never_enlarged()
    {
        // Upscaling adds no detail and costs the detector time proportional to the area it is given.
        var decoded = await _decoder.DecodeAsync(Fixture("upright.jpg"), maxLongEdge: 4000, default);

        decoded!.Buffer.Width.Should().Be(40);
        decoded.Scale.Should().Be(1f);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_returns_nothing_rather_than_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-an-image-{Guid.NewGuid():N}.jpg");
        await File.WriteAllTextAsync(path, "this is not a JPEG");

        try
        {
            (await _decoder.DecodeAsync(path, null, default)).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_missing_file_returns_nothing_rather_than_throwing() =>
        (await _decoder.DecodeAsync(Fixture("does-not-exist.jpg"), null, default)).Should().BeNull();

    [Fact]
    public void Unsupported_extensions_are_not_claimed()
    {
        _decoder.CanDecode("movie.mp4").Should().BeFalse();
        _decoder.CanDecode("photo.jpg").Should().BeTrue();
        _decoder.CanDecode("photo.HEIC").Should().BeTrue();
    }
}
