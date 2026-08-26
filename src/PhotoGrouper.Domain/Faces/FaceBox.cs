using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Domain.Faces;

/// <summary>
/// Where a face sits in a photo, in pixels of the orientation-corrected image.
/// </summary>
/// <remarks>
/// Coordinates are always relative to the image after its EXIF orientation has been applied.
/// Storing them against the raw stored pixels instead would leave every box on a portrait
/// phone photo rotated ninety degrees away from the face it describes.
/// </remarks>
public readonly record struct FaceBox(float X, float Y, float Width, float Height, float Score)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public Point2 Center => new(X + (Width / 2f), Y + (Height / 2f));

    public float Area => Width * Height;

    /// <summary>The shorter side, used as the face's size for quality gating.</summary>
    public float SmallestSide => MathF.Min(Width, Height);

    /// <summary>
    /// Intersection over union with another box.
    /// </summary>
    /// <remarks>
    /// The basis for carrying person assignments across a detector change: two detectors
    /// find slightly different boxes for the same face, and a high overlap is what identifies
    /// them as the same face rather than a new one.
    /// </remarks>
    public float IntersectionOverUnion(FaceBox other)
    {
        var left = MathF.Max(X, other.X);
        var top = MathF.Max(Y, other.Y);
        var right = MathF.Min(Right, other.Right);
        var bottom = MathF.Min(Bottom, other.Bottom);

        if (right <= left || bottom <= top)
        {
            return 0f;
        }

        var intersection = (right - left) * (bottom - top);
        var union = Area + other.Area - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    /// <summary>Grows the box by a proportion of its size, clamped to the image.</summary>
    public FaceBox Expand(float proportion, int imageWidth, int imageHeight)
    {
        var dx = Width * proportion;
        var dy = Height * proportion;

        var x = MathF.Max(0, X - dx);
        var y = MathF.Max(0, Y - dy);
        var right = MathF.Min(imageWidth, Right + dx);
        var bottom = MathF.Min(imageHeight, Bottom + dy);

        return new FaceBox(x, y, right - x, bottom - y, Score);
    }
}
