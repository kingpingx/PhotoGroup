using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// The least-squares similarity transform between two sets of points.
/// </summary>
/// <remarks>
/// This is what turns a face found anywhere in a photograph, at any size and angle, into the
/// fixed 112 pixel square an embedder was trained on. It solves for the rotation, uniform scale
/// and translation that best carry the five detected landmarks onto the model's reference
/// template, in the least-squares sense of Umeyama's 1991 method.
///
/// Reflection is deliberately excluded. The general form of the method permits a mirror when that
/// fits the points better, which for a face means quietly flipping it left to right; the crop
/// still looks like a face and the embedding is still well-formed, but it describes a mirror
/// image and will not match the same person elsewhere. Restricting the solution to rotations
/// makes that outcome unrepresentable rather than merely unlikely.
///
/// Solved in closed form rather than through a singular value decomposition. In two dimensions
/// the reflection-free case reduces to two dot products, which is exact, allocation-free, and has
/// no iterative step to converge badly on degenerate input.
/// </remarks>
public readonly record struct SimilarityTransform(float M11, float M12, float M21, float M22, float Dx, float Dy)
{
    /// <summary>The transform that leaves every point where it is.</summary>
    public static SimilarityTransform Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>The uniform scale factor this transform applies.</summary>
    public float Scale => MathF.Sqrt((M11 * M11) + (M21 * M21));

    /// <summary>The rotation this transform applies, in radians.</summary>
    public float RotationRadians => MathF.Atan2(M21, M11);

    public Point2 Apply(Point2 point) => new(
        (M11 * point.X) + (M12 * point.Y) + Dx,
        (M21 * point.X) + (M22 * point.Y) + Dy);

    /// <summary>The transform as a 2x3 affine matrix.</summary>
    public double[,] ToAffineMatrix() => new double[,]
    {
        { M11, M12, Dx },
        { M21, M22, Dy },
    };

    /// <summary>
    /// Solves for the transform carrying <paramref name="source"/> onto <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Degenerate input, meaning every source point at the same place, has no meaningful scale or
    /// rotation. That happens when a detector returns collapsed landmarks for a tiny or heavily
    /// occluded face. The identity is returned rather than a division by zero, and the resulting
    /// crop will simply fail the quality gate.
    /// </remarks>
    public static SimilarityTransform Solve(ReadOnlySpan<Point2> source, ReadOnlySpan<Point2> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException(
                "Source and destination must contain the same number of points.", nameof(destination));
        }

        if (source.Length < 2)
        {
            throw new ArgumentException("At least two points are required.", nameof(source));
        }

        var count = source.Length;

        var sourceMean = Mean(source);
        var destinationMean = Mean(destination);

        // Accumulated about the centroids, so translation drops out and only rotation and scale
        // remain to be found.
        double dot = 0;
        double cross = 0;
        double sourceEnergy = 0;

        for (var i = 0; i < count; i++)
        {
            double ax = source[i].X - sourceMean.X;
            double ay = source[i].Y - sourceMean.Y;
            double bx = destination[i].X - destinationMean.X;
            double by = destination[i].Y - destinationMean.Y;

            dot += (ax * bx) + (ay * by);
            cross += (ax * by) - (ay * bx);
            sourceEnergy += (ax * ax) + (ay * ay);
        }

        if (sourceEnergy <= double.Epsilon)
        {
            return Identity;
        }

        // dot yields scale times cosine of the rotation, cross yields scale times its sine. Taken
        // together they are the rotation and scale directly, with no decomposition needed.
        var scaleCos = dot / sourceEnergy;
        var scaleSin = cross / sourceEnergy;

        var dx = destinationMean.X - ((scaleCos * sourceMean.X) - (scaleSin * sourceMean.Y));
        var dy = destinationMean.Y - ((scaleSin * sourceMean.X) + (scaleCos * sourceMean.Y));

        return new SimilarityTransform(
            (float)scaleCos, (float)(-scaleSin),
            (float)scaleSin, (float)scaleCos,
            (float)dx, (float)dy);
    }

    private static (double X, double Y) Mean(ReadOnlySpan<Point2> points)
    {
        double x = 0;
        double y = 0;

        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return (x / points.Length, y / points.Length);
    }
}
