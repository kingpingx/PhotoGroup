using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Photos;

/// <summary>
/// Reduces a decoded photograph to what is needed to tell near-duplicates apart.
/// </summary>
/// <remarks>
/// Lives in the application layer beside the clustering algorithm, and for the same reason: this
/// is a rule about how the product decides two photographs are the same picture, not a detail of
/// how pixels are decoded. It takes the framework-neutral buffer and returns numbers, so it can be
/// tested without a decoder, a database or a screen.
///
/// The method is the difference hash, taken in both directions. The image is reduced to a small
/// grey grid and each point compared with its neighbour, giving bits that describe where the image
/// gets lighter and darker rather than what colour or brightness it is. That is what makes it
/// survive re-compression, resizing and a change of exposure, while still separating two different
/// scenes.
///
/// Two readings, because one is not enough. Comparing neighbours cannot tell a gradient from a
/// flat wall — both answer "no point is brighter than the next" — so a picture and its own
/// transposition come out identical. The second reading records where the light actually is,
/// relative to the frame's own average, which separates exactly those cases while staying
/// indifferent to exposure, since shifting every pixel shifts the average with it.
///
/// It is deliberately not a face or object comparison. Two photographs of the same person in
/// different rooms are different pictures, and a tool that offered to delete one of them because
/// the subject matched would be dangerous.
/// </remarks>
public static class PhotoSignatures
{
    /// <summary>
    /// The reduced grid each half of the fingerprint is taken from.
    /// </summary>
    /// <remarks>
    /// Nine by eight, so that comparing each point with its right-hand neighbour yields exactly
    /// sixty-four bits. The vertical half uses the transpose of these for the same reason.
    /// </remarks>
    private const int HashLong = 9;

    private const int HashShort = 8;

    /// <summary>Side of the grid used to judge sharpness.</summary>
    /// <remarks>
    /// Larger than the hash grid, because sharpness is exactly the fine detail the hash throws
    /// away. Small enough that the measurement costs nothing beside the decode that preceded it.
    /// </remarks>
    private const int SharpnessSide = 48;

    /// <summary>The fingerprint of what this photograph looks like.</summary>
    public static PerceptualHash Hash(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return new PerceptualHash(GradientBits(image), BrightnessBits(image));
    }

    /// <summary>Where the image gets lighter and darker from one point to the next.</summary>
    private static ulong GradientBits(ImageBuffer image)
    {
        var grid = Reduce(image, HashLong, HashShort);
        var bits = 0UL;
        var bit = 0;

        for (var y = 0; y < HashShort; y++)
        {
            for (var x = 0; x < HashLong - 1; x++)
            {
                if (grid[(y * HashLong) + x] > grid[(y * HashLong) + x + 1])
                {
                    bits |= 1UL << bit;
                }

                bit++;
            }
        }

        return bits;
    }

    /// <summary>
    /// Which cells of the frame are lighter than the frame's own average.
    /// </summary>
    /// <remarks>
    /// Compared against the average rather than against a fixed level, which is what makes it
    /// survive a change of exposure: adding light to every pixel moves the average by the same
    /// amount and leaves every comparison as it was.
    ///
    /// This is the half that separates a gradient from a flat wall, and a picture from its
    /// transposition. Where the neighbour comparison answers "no" for both, this describes where
    /// in the frame the light sits, which is different in each.
    /// </remarks>
    private static ulong BrightnessBits(ImageBuffer image)
    {
        var grid = Reduce(image, HashShort, HashShort);

        var mean = 0d;
        foreach (var cell in grid)
        {
            mean += cell;
        }

        mean /= grid.Length;

        var bits = 0UL;
        for (var i = 0; i < grid.Length; i++)
        {
            if (grid[i] > mean)
            {
                bits |= 1UL << i;
            }
        }

        return bits;
    }

    /// <summary>
    /// How much fine detail the photograph carries; higher is sharper.
    /// </summary>
    /// <remarks>
    /// The variance of a Laplacian, which is the standard cheap answer: a blurred image has little
    /// local contrast, so the second derivative is near zero everywhere and its variance collapses.
    /// It exists to choose between near-identical frames, which is precisely where it is reliable —
    /// the same scene, the same exposure, one of them softer. Comparing the sharpness of two
    /// unrelated photographs is not meaningful, because a picture of a brick wall scores higher
    /// than a portrait against the sky whatever the focus.
    /// </remarks>
    public static double Sharpness(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var grid = Reduce(image, SharpnessSide, SharpnessSide);

        var sum = 0d;
        var sumOfSquares = 0d;
        var counted = 0;

        for (var y = 1; y < SharpnessSide - 1; y++)
        {
            for (var x = 1; x < SharpnessSide - 1; x++)
            {
                var centre = grid[(y * SharpnessSide) + x];

                var laplacian =
                    (4 * centre)
                    - grid[((y - 1) * SharpnessSide) + x]
                    - grid[((y + 1) * SharpnessSide) + x]
                    - grid[(y * SharpnessSide) + x - 1]
                    - grid[(y * SharpnessSide) + x + 1];

                sum += laplacian;
                sumOfSquares += (double)laplacian * laplacian;
                counted++;
            }
        }

        if (counted == 0)
        {
            return 0;
        }

        var mean = sum / counted;
        return Math.Max(0, (sumOfSquares / counted) - (mean * mean));
    }

    /// <summary>
    /// Averages the image down to a small grey grid.
    /// </summary>
    /// <remarks>
    /// Averaging every source pixel that falls in a cell, rather than sampling one of them. A
    /// sampled reduction is faster and wrong for this purpose: it makes the fingerprint depend on
    /// which pixels happened to be picked, so the same picture at two resolutions lands on
    /// different bits, which is the one thing this must not do.
    /// </remarks>
    private static float[] Reduce(ImageBuffer image, int columns, int rows)
    {
        var totals = new double[columns * rows];
        var counts = new int[columns * rows];
        var bytesPerPixel = ImageBuffer.BytesPerPixel(image.Format);
        var pixels = image.Pixels.Span;

        for (var y = 0; y < image.Height; y++)
        {
            // Clamped because the last row and column would otherwise index one cell past the end
            // whenever the image size divides exactly into the grid.
            var cellY = Math.Min(rows - 1, y * rows / image.Height);
            var row = y * image.Stride;

            for (var x = 0; x < image.Width; x++)
            {
                var cellX = Math.Min(columns - 1, x * columns / image.Width);
                var offset = row + (x * bytesPerPixel);

                // Weighted for perceived brightness rather than averaged flat. A flat average
                // makes saturated red and saturated blue the same shade of grey, and a picture
                // that changes only in colour would then produce an identical fingerprint.
                var value = bytesPerPixel == 1
                    ? pixels[offset]
                    : image.Format == PixelFormat.Bgr24
                        ? (0.114 * pixels[offset]) + (0.587 * pixels[offset + 1]) + (0.299 * pixels[offset + 2])
                        : (0.299 * pixels[offset]) + (0.587 * pixels[offset + 1]) + (0.114 * pixels[offset + 2]);

                var cell = (cellY * columns) + cellX;
                totals[cell] += value;
                counts[cell]++;
            }
        }

        var grid = new float[columns * rows];
        for (var i = 0; i < grid.Length; i++)
        {
            grid[i] = counts[i] == 0 ? 0f : (float)(totals[i] / counts[i]);
        }

        return grid;
    }
}
