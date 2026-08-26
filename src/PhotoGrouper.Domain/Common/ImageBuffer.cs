namespace PhotoGrouper.Domain.Common;

/// <summary>
/// Raw decoded pixels, framework-neutral.
/// </summary>
/// <remarks>
/// This is the type pixels use to cross a layer boundary. The vision ports could have
/// been typed in terms of OpenCV's Mat, which would avoid a copy, but that would drag
/// OpenCvSharp into the application layer and make every use case depend on a native
/// imaging library. At the volumes this app handles the copy is negligible beside JPEG
/// decode; if profiling ever says otherwise the answer is a pooled backing array, not a
/// weaker boundary.
/// </remarks>
public sealed class ImageBuffer
{
    public ImageBuffer(int width, int height, int stride, PixelFormat format, ReadOnlyMemory<byte> pixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var minimumStride = width * BytesPerPixel(format);
        if (stride < minimumStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride), stride, $"Stride must be at least {minimumStride} for a {width}px wide {format} image.");
        }

        var required = (long)stride * height;
        if (pixels.Length < required)
        {
            throw new ArgumentException(
                $"Pixel buffer holds {pixels.Length} bytes but {required} are required.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row, which may exceed <see cref="Width"/> times the pixel size when rows are padded.</summary>
    public int Stride { get; }

    public PixelFormat Format { get; }

    public ReadOnlyMemory<byte> Pixels { get; }

    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Bgr24 or PixelFormat.Rgb24 => 3,
        PixelFormat.Gray8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };

    /// <summary>Returns the pixels of a single row.</summary>
    public ReadOnlySpan<byte> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return Pixels.Span.Slice(y * Stride, Width * BytesPerPixel(Format));
    }
}
