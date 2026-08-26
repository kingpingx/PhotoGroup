using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Fits an image of any shape into a detector's fixed square input, and maps results back.
/// </summary>
/// <remarks>
/// Both detectors here take a fixed 640 by 640 tensor. Stretching a photo to fill it would
/// distort every face, and detectors are trained on undistorted ones, so recall falls sharply.
/// Instead the image is scaled to fit and the remainder padded, preserving aspect ratio.
///
/// The consequence is that every coordinate the model returns is in padded space and means
/// nothing until it is mapped back. Omitting that step produces boxes that are plausibly
/// positioned but consistently offset, which reads as a poor detector rather than a missing
/// transform.
/// </remarks>
public sealed record Letterbox(int SourceWidth, int SourceHeight, int TargetSize, float Scale, int PadX, int PadY)
{
    /// <summary>
    /// Computes the fit for a source image.
    /// </summary>
    /// <remarks>
    /// Padding goes entirely on the right and bottom rather than being centred. Both work, but
    /// only if the inverse mapping agrees; anchoring at the origin makes the inverse a plain
    /// division and removes a place to be inconsistent.
    /// </remarks>
    public static Letterbox Fit(int sourceWidth, int sourceHeight, int targetSize)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        }

        var scale = MathF.Min((float)targetSize / sourceWidth, (float)targetSize / sourceHeight);

        var scaledWidth = Math.Min(targetSize, (int)MathF.Round(sourceWidth * scale));
        var scaledHeight = Math.Min(targetSize, (int)MathF.Round(sourceHeight * scale));

        return new Letterbox(
            sourceWidth, sourceHeight, targetSize, scale,
            PadX: targetSize - scaledWidth,
            PadY: targetSize - scaledHeight);
    }

    /// <summary>Width of the image content inside the padded square.</summary>
    public int ScaledWidth => TargetSize - PadX;

    /// <summary>Height of the image content inside the padded square.</summary>
    public int ScaledHeight => TargetSize - PadY;

    /// <summary>Maps a point from padded model space back to source image coordinates.</summary>
    public Point2 ToSource(Point2 point) => new(point.X / Scale, point.Y / Scale);

    /// <summary>Maps a box from padded model space back to source image coordinates, clamped to the image.</summary>
    public FaceBox ToSource(FaceBox box)
    {
        var x = box.X / Scale;
        var y = box.Y / Scale;
        var right = box.Right / Scale;
        var bottom = box.Bottom / Scale;

        // A detector will happily place part of a box beyond the edge of the image for a face
        // that is partly out of frame. Clamping keeps every stored box addressable as a crop.
        x = Math.Clamp(x, 0, SourceWidth);
        y = Math.Clamp(y, 0, SourceHeight);
        right = Math.Clamp(right, 0, SourceWidth);
        bottom = Math.Clamp(bottom, 0, SourceHeight);

        return new FaceBox(x, y, MathF.Max(0, right - x), MathF.Max(0, bottom - y), box.Score);
    }

    /// <summary>Maps landmarks from padded model space back to source image coordinates.</summary>
    /// <remarks>
    /// Not clamped, unlike boxes. A landmark just outside the frame is a real estimate of where
    /// an occluded eye lies, and alignment uses it as such; pinning it to the edge would pull
    /// the whole transform askew.
    /// </remarks>
    public FaceLandmarks ToSource(FaceLandmarks landmarks) => new(
        ToSource(landmarks.LeftEye),
        ToSource(landmarks.RightEye),
        ToSource(landmarks.Nose),
        ToSource(landmarks.MouthLeft),
        ToSource(landmarks.MouthRight));
}
