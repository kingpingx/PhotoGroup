using FluentAssertions;
using PhotoGrouper.Application.Photos;
using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Application.Tests;

/// <summary>
/// Covers reducing a photograph to a fingerprint.
/// </summary>
/// <remarks>
/// This is the number a user's files are moved on the strength of, so the properties worth pinning
/// are the ones that decide whether the answer is trustworthy: the same picture at two sizes must
/// agree, a picture and a different picture must not, and a small change must move the value a
/// small amount rather than all of it.
///
/// Built from synthetic images rather than photographs, so a failure points at the algorithm
/// rather than at a fixture. Gradients and blocks are what the difference hash is defined over —
/// where the image gets lighter and darker — so they exercise it directly.
/// </remarks>
public sealed class PhotoSignatureTests
{
    /// <summary>Builds a greyscale image from a function of position, in 0 to 255.</summary>
    private static ImageBuffer Gray(int width, int height, Func<double, double, byte> shade)
    {
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Passed as fractions of the image rather than as pixels, so the same picture can
                // be built at any resolution and is genuinely the same picture.
                pixels[(y * width) + x] = shade((x + 0.5) / width, (y + 0.5) / height);
            }
        }

        return new ImageBuffer(width, height, width, PixelFormat.Gray8, pixels);
    }

    private static byte HorizontalRamp(double x, double y) => (byte)(x * 255);

    private static byte VerticalRamp(double x, double y) => (byte)(y * 255);

    private static byte Checkerboard(double x, double y) =>
        (byte)((((int)(x * 8) + (int)(y * 8)) % 2 == 0) ? 20 : 235);

    /// <summary>
    /// A stand-in for a photograph: a few soft blobs of differing brightness.
    /// </summary>
    /// <remarks>
    /// Used where a test is about how the fingerprint behaves on real pictures rather than about
    /// one of its edges. A ramp or a checkerboard is a degenerate image, and a threshold justified
    /// only against those would say nothing about a library of photographs.
    /// </remarks>
    private static byte Scene(double x, double y)
    {
        var value = 60d
            + (150 * Math.Exp(-40 * (((x - 0.30) * (x - 0.30)) + ((y - 0.35) * (y - 0.35)))))
            + (110 * Math.Exp(-25 * (((x - 0.72) * (x - 0.72)) + ((y - 0.62) * (y - 0.62)))))
            + (60 * Math.Exp(-80 * (((x - 0.55) * (x - 0.55)) + ((y - 0.18) * (y - 0.18)))));

        return (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>The same scene a moment later: the frame has shifted and one region has changed.</summary>
    private static byte SceneAMomentLater(double x, double y)
    {
        var shifted = Scene(x + 0.02, y + 0.01);

        return x is > 0.26 and < 0.34 && y is > 0.30 and < 0.36
            ? (byte)Math.Clamp(shifted - 70, 0, 255)
            : shifted;
    }

    private static byte DifferentScene(double x, double y)
    {
        var value = 90d
            + (130 * Math.Exp(-30 * (((x - 0.80) * (x - 0.80)) + ((y - 0.20) * (y - 0.20)))))
            + (90 * Math.Exp(-45 * (((x - 0.25) * (x - 0.25)) + ((y - 0.80) * (y - 0.80)))));

        return (byte)Math.Clamp(value, 0, 255);
    }

    [Fact]
    public void The_same_picture_at_two_resolutions_has_the_same_fingerprint()
    {
        // The property the whole feature rests on. A copy re-saved at half the size is the same
        // picture, and a fingerprint that disagreed would report it as unrelated.
        var large = PhotoSignatures.Hash(Gray(800, 600, HorizontalRamp));
        var small = PhotoSignatures.Hash(Gray(200, 150, HorizontalRamp));

        small.Should().Be(large);
    }

    [Fact]
    public void An_identical_image_has_no_distance_from_itself()
    {
        var hash = PhotoSignatures.Hash(Gray(320, 240, Checkerboard));

        hash.DistanceTo(hash).Should().Be(0);
    }

    /// <remarks>
    /// A transposition rather than two unrelated scenes, deliberately. The two images have the
    /// identical histogram and differ only in which way the light runs, which is exactly the pair a
    /// fingerprint taken in one direction reports as identical — both reduce to "no point is
    /// brighter than the one to its right". It is the case that made a single-direction hash unfit
    /// for deciding which of somebody's files to move.
    /// </remarks>
    [Fact]
    public void A_picture_and_its_transposition_are_not_the_same_picture()
    {
        var horizontal = PhotoSignatures.Hash(Gray(400, 400, HorizontalRamp));
        var vertical = PhotoSignatures.Hash(Gray(400, 400, VerticalRamp));

        horizontal.DistanceTo(vertical).Should()
            .BeGreaterThan(FindDuplicatesFixture.Threshold,
                "a picture and its transposition are different pictures");
    }

    /// <remarks>
    /// The case the whole feature exists for: a second frame of one scene, the camera moved a
    /// little and something in the picture changed. The fingerprint must move by a little — not by
    /// nothing, which would mean it cannot see change at all, and not past the threshold, which
    /// would mean a burst is never recognised as one.
    /// </remarks>
    [Fact]
    public void A_second_frame_of_one_scene_stays_within_the_threshold()
    {
        var first = PhotoSignatures.Hash(Gray(800, 600, Scene));
        var second = PhotoSignatures.Hash(Gray(800, 600, SceneAMomentLater));

        var distance = first.DistanceTo(second);

        distance.Should().BeGreaterThan(0, "the picture did change");
        distance.Should().BeLessThanOrEqualTo(FindDuplicatesFixture.Threshold,
            "a shifted frame of the same scene is the same picture");
    }

    /// <remarks>
    /// The property the threshold is chosen from, and the reason it can be chosen at all. What
    /// matters is not either number but the gap between them: if a burst and two unrelated
    /// photographs scored anywhere near each other there would be no safe line to draw, and this
    /// feature could not move files on the strength of it.
    /// </remarks>
    [Fact]
    public void A_burst_scores_far_closer_than_two_different_scenes()
    {
        var scene = PhotoSignatures.Hash(Gray(800, 600, Scene));
        var burst = scene.DistanceTo(PhotoSignatures.Hash(Gray(800, 600, SceneAMomentLater)));
        var unrelated = scene.DistanceTo(PhotoSignatures.Hash(Gray(800, 600, DifferentScene)));

        burst.Should().BeLessThan(unrelated / 4,
            "the gap is what makes a threshold between them meaningful");

        unrelated.Should().BeGreaterThan(FindDuplicatesFixture.Threshold * 2,
            "the threshold must sit well inside the gap, not at the edge of it");
    }

    [Fact]
    public void Brightening_the_whole_image_leaves_the_fingerprint_alone()
    {
        // What survives re-compression and an exposure tweak. The fingerprint records where the
        // image gets lighter and darker, not how light it is, so a uniform shift changes nothing.
        var normal = PhotoSignatures.Hash(Gray(400, 300, (x, y) => (byte)(30 + (x * 100))));
        var brighter = PhotoSignatures.Hash(Gray(400, 300, (x, y) => (byte)(80 + (x * 100))));

        brighter.Should().Be(normal);
    }

    /// <remarks>
    /// Under a flat average of the three channels, saturated red and saturated blue are the same
    /// shade of grey, so these two images would reduce to the same flat rectangle and fingerprint
    /// identically. Weighted for perceived brightness, red is much the lighter, and the two are
    /// mirror images of each other.
    ///
    /// Note what this does not claim: the fingerprint records where an image gets lighter, so red
    /// beside black and blue beside black remain the same picture to it. Colour matters only where
    /// it changes which side is brighter.
    /// </remarks>
    [Fact]
    public void Colour_is_weighted_for_how_bright_it_looks()
    {
        var redThenBlue = Rgb(120, 90, (x, y) =>
            x < 0.5 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));

        var blueThenRed = Rgb(120, 90, (x, y) =>
            x < 0.5 ? ((byte)0, (byte)0, (byte)255) : ((byte)255, (byte)0, (byte)0));

        PhotoSignatures.Hash(redThenBlue).Should().NotBe(PhotoSignatures.Hash(blueThenRed));
    }

    private static ImageBuffer Rgb(int width, int height, Func<double, double, (byte R, byte G, byte B)> shade)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = shade((x + 0.5) / width, (y + 0.5) / height);
                var offset = (y * stride) + (x * 3);
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
            }
        }

        return new ImageBuffer(width, height, stride, PixelFormat.Bgr24, pixels);
    }

    /// <remarks>
    /// Sharpness exists to choose between frames of one scene, which is the only comparison it is
    /// reliable for. A blurred copy of a picture must score below the crisp one; comparing two
    /// unrelated photographs is not meaningful and is not claimed anywhere.
    /// </remarks>
    [Fact]
    public void A_blurred_picture_scores_below_the_crisp_one()
    {
        var crisp = PhotoSignatures.Sharpness(Gray(400, 400, Checkerboard));

        // The same checkerboard with the edges smeared, which is what defocus does.
        var blurred = PhotoSignatures.Sharpness(Gray(400, 400, (x, y) =>
        {
            var wave = (Math.Sin(x * 8 * Math.PI) + Math.Sin(y * 8 * Math.PI)) / 2;
            return (byte)(128 + (wave * 40));
        }));

        blurred.Should().BeLessThan(crisp);
    }

    [Fact]
    public void A_flat_image_carries_no_detail()
    {
        PhotoSignatures.Sharpness(Gray(200, 200, (x, y) => 128))
            .Should().BeApproximately(0, 0.0001);
    }
}

/// <summary>The threshold these tests are written against, kept in one place.</summary>
internal static class FindDuplicatesFixture
{
    public const int Threshold = 12;
}
