using OpenCvSharp;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>
/// Applies an EXIF orientation tag to decoded pixels.
/// </summary>
/// <remarks>
/// Cameras rarely rotate pixels when a photo is taken. They store the sensor's own landscape
/// output and record how it should be turned for display. Almost every photo taken in portrait
/// on a phone therefore arrives sideways, and a viewer that honours the tag makes this
/// invisible — which is precisely why it is so easy to omit.
///
/// The consequence for this app is not cosmetic. Detection on unrotated pixels finds far fewer
/// faces, since detectors are trained on upright ones, and any it does find gets a bounding box
/// in coordinates that do not correspond to what the user sees. Both failures look like a poor
/// detector rather than a missing transform.
///
/// The eight tag values cover four rotations and their mirrored counterparts. The mirrored ones
/// are rare but real: they come from front-facing cameras and from some scanning software.
/// </remarks>
public static class OrientationTransform
{
    /// <summary>Returns an upright copy, or the original Mat when no transform is needed.</summary>
    /// <remarks>
    /// Values outside 1 to 8 are treated as upright rather than rejected. A corrupt tag should
    /// cost a photo its rotation, not its place in the library.
    /// </remarks>
    public static Mat Apply(Mat source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (orientation)
        {
            case 2:
                return Flip(source, FlipMode.Y);

            case 3:
                return Rotate(source, RotateFlags.Rotate180);

            case 4:
                return Flip(source, FlipMode.X);

            case 5:
                // Transpose is a reflection in the main diagonal, which is a quarter turn
                // combined with a mirror. Used here for the two "transposed" tags.
                return Transpose(source);

            case 6:
                return Rotate(source, RotateFlags.Rotate90Clockwise);

            case 7:
                return Transverse(source);

            case 8:
                return Rotate(source, RotateFlags.Rotate90Counterclockwise);

            default:
                return source;
        }
    }

    /// <summary>True when the tag calls for any transform at all.</summary>
    public static bool IsIdentity(int orientation) => orientation is < 2 or > 8;

    private static Mat Rotate(Mat source, RotateFlags flags)
    {
        var result = new Mat();
        Cv2.Rotate(source, result, flags);
        return result;
    }

    private static Mat Flip(Mat source, FlipMode mode)
    {
        var result = new Mat();
        Cv2.Flip(source, result, mode);
        return result;
    }

    private static Mat Transpose(Mat source)
    {
        var result = new Mat();
        Cv2.Transpose(source, result);
        return result;
    }

    private static Mat Transverse(Mat source)
    {
        // Reflection in the anti-diagonal: transpose, then flip both axes.
        using var transposed = new Mat();
        Cv2.Transpose(source, transposed);

        var result = new Mat();
        Cv2.Flip(transposed, result, FlipMode.XY);
        return result;
    }
}
