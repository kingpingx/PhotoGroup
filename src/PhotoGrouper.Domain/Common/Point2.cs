namespace PhotoGrouper.Domain.Common;

/// <summary>A point in image coordinates, in pixels from the top-left.</summary>
/// <remarks>
/// Defined here rather than reusing System.Drawing.PointF so the domain carries its own
/// vocabulary and nothing in the inner layers implies a graphics stack.
/// </remarks>
public readonly record struct Point2(float X, float Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Point2 operator *(Point2 p, float scale) => new(p.X * scale, p.Y * scale);

    public float DistanceTo(Point2 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() => $"({X:F1}, {Y:F1})";
}
