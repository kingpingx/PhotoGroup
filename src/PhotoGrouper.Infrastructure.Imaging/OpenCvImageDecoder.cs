using OpenCvSharp;
using PhotoGrouper.Application.Abstractions;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>Decodes the formats OpenCV handles natively.</summary>
public sealed class OpenCvImageDecoder : IImageDecoder
{
    public bool CanDecode(string path) =>
        SupportedImageFormats.Standard.Contains(Path.GetExtension(path));

    public Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken ct) =>
        Task.Run(() => ExifReader.Read(path) ?? ReadDimensionsByDecoding(path), ct);

    public Task<DecodedImage?> DecodeAsync(string path, int? maxLongEdge, CancellationToken ct) =>
        Task.Run(() => Decode(path, maxLongEdge), ct);

    private static DecodedImage? Decode(string path, int? maxLongEdge)
    {
        Mat? raw = null;
        Mat? upright = null;
        Mat? scaled = null;

        try
        {
            // IgnoreOrientation is deliberate. OpenCV will honour the EXIF tag itself when
            // asked, but the HEIC path goes through a different library, and two decoders
            // rotating by their own rules is how one format ends up sideways. Rotation is
            // applied here instead, identically for every format, from a tag this code read.
            raw = Cv2.ImRead(path, ImreadModes.Color | ImreadModes.IgnoreOrientation);
            if (raw.Empty())
            {
                return null;
            }

            var orientation = ExifReader.Read(path)?.Orientation ?? ExifReader.DefaultOrientation;
            upright = OrientationTransform.Apply(raw, orientation);

            var originalWidth = upright.Width;
            var originalHeight = upright.Height;

            var scale = ComputeScale(originalWidth, originalHeight, maxLongEdge);
            if (scale < 1f)
            {
                scaled = new Mat();
                Cv2.Resize(
                    upright,
                    scaled,
                    new OpenCvSharp.Size(
                        Math.Max(1, (int)MathF.Round(originalWidth * scale)),
                        Math.Max(1, (int)MathF.Round(originalHeight * scale))),
                    interpolation: InterpolationFlags.Area);
            }

            var buffer = MatBridge.ToImageBuffer(scaled ?? upright);
            return new DecodedImage(buffer, originalWidth, originalHeight, scale);
        }
        catch (Exception e) when (e is OpenCVException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            scaled?.Dispose();

            // Apply returns the source unchanged when no rotation is needed, so disposing both
            // references would be a double dispose in the common case.
            if (!ReferenceEquals(upright, raw))
            {
                upright?.Dispose();
            }

            raw?.Dispose();
        }
    }

    /// <summary>Factor to bring the long edge within the limit; 1 when no downscale is needed.</summary>
    /// <remarks>
    /// Never scales up. A small image enlarged to the detection size gains no detail and costs
    /// the detector time proportional to the area it was given.
    /// </remarks>
    internal static float ComputeScale(int width, int height, int? maxLongEdge)
    {
        if (maxLongEdge is not { } limit || limit <= 0)
        {
            return 1f;
        }

        var longEdge = Math.Max(width, height);
        return longEdge <= limit ? 1f : (float)limit / longEdge;
    }

    /// <summary>
    /// Falls back to decoding when a file carries no usable EXIF.
    /// </summary>
    /// <remarks>
    /// PNG, BMP and files stripped of metadata have no EXIF block at all. Without this a
    /// perfectly readable photo would be recorded as having no dimensions.
    /// </remarks>
    private static ImageMetadata? ReadDimensionsByDecoding(string path)
    {
        try
        {
            using var mat = Cv2.ImRead(path, ImreadModes.Color | ImreadModes.IgnoreOrientation);
            return mat.Empty()
                ? null
                : new ImageMetadata(mat.Width, mat.Height, ExifReader.DefaultOrientation, null, null);
        }
        catch (OpenCVException)
        {
            return null;
        }
    }
}
