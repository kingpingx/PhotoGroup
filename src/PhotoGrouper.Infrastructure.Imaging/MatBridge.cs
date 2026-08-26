using System.Runtime.InteropServices;
using OpenCvSharp;
using PhotoGrouper.Domain.Common;
using PixelFormat = PhotoGrouper.Domain.Common.PixelFormat;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>Converts between OpenCV's Mat and the domain's framework-neutral pixel buffer.</summary>
/// <remarks>
/// The single place where an OpenCV type meets a domain type. Everything above this file works
/// in <see cref="ImageBuffer"/>, which is what keeps the native imaging library out of the
/// application layer.
/// </remarks>
public static class MatBridge
{
    /// <summary>Copies a Mat into a tightly packed buffer.</summary>
    /// <remarks>
    /// Copies row by row rather than in one block because a Mat's rows can be padded, and a Mat
    /// produced by cropping shares its parent's stride while covering only part of each row. A
    /// single bulk copy of such a Mat silently interleaves neighbouring pixels into the result.
    /// </remarks>
    public static ImageBuffer ToImageBuffer(Mat mat)
    {
        ArgumentNullException.ThrowIfNull(mat);

        if (mat.Empty())
        {
            throw new ArgumentException("Cannot convert an empty Mat.", nameof(mat));
        }

        var format = mat.Type() switch
        {
            var t when t == MatType.CV_8UC3 => PixelFormat.Bgr24,
            var t when t == MatType.CV_8UC1 => PixelFormat.Gray8,
            var t => throw new ArgumentException($"Unsupported Mat type {t}.", nameof(mat)),
        };

        var bytesPerPixel = ImageBuffer.BytesPerPixel(format);
        var stride = mat.Width * bytesPerPixel;
        var pixels = new byte[stride * mat.Height];

        for (var y = 0; y < mat.Height; y++)
        {
            Marshal.Copy(mat.Ptr(y), pixels, y * stride, stride);
        }

        return new ImageBuffer(mat.Width, mat.Height, stride, format, pixels);
    }

    /// <summary>
    /// Wraps a buffer as a Mat.
    /// </summary>
    /// <remarks>
    /// The returned Mat owns its own copy. Pointing a Mat at the buffer's memory would be
    /// faster but leaves a native object referencing a managed array, which the collector is
    /// free to move.
    /// </remarks>
    public static Mat ToMat(ImageBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var type = buffer.Format switch
        {
            PixelFormat.Bgr24 or PixelFormat.Rgb24 => MatType.CV_8UC3,
            PixelFormat.Gray8 => MatType.CV_8UC1,
            _ => throw new ArgumentException($"Unsupported pixel format {buffer.Format}.", nameof(buffer)),
        };

        var mat = new Mat(buffer.Height, buffer.Width, type);
        var bytesPerRow = buffer.Width * ImageBuffer.BytesPerPixel(buffer.Format);

        // Copied straight out of the backing array where one is available. The obvious
        // alternative, slicing the span per row and calling ToArray, allocates a fresh array for
        // every row of every image; at pipeline volumes that is the single largest source of
        // garbage in the whole detection path.
        if (MemoryMarshal.TryGetArray(buffer.Pixels, out var segment) && segment.Array is { } array)
        {
            for (var y = 0; y < buffer.Height; y++)
            {
                Marshal.Copy(array, segment.Offset + (y * buffer.Stride), mat.Ptr(y), bytesPerRow);
            }
        }
        else
        {
            var scratch = new byte[bytesPerRow];
            var span = buffer.Pixels.Span;

            for (var y = 0; y < buffer.Height; y++)
            {
                span.Slice(y * buffer.Stride, bytesPerRow).CopyTo(scratch);
                Marshal.Copy(scratch, 0, mat.Ptr(y), bytesPerRow);
            }
        }

        // The domain distinguishes RGB from BGR; OpenCV assumes BGR for three-channel data.
        // Converting here means an RGB buffer handed to an OpenCV operation is not silently
        // treated as though its red and blue channels were swapped.
        if (buffer.Format == PixelFormat.Rgb24)
        {
            Cv2.CvtColor(mat, mat, ColorConversionCodes.RGB2BGR);
        }

        return mat;
    }
}
