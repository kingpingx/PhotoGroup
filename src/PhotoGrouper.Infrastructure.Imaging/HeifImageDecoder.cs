using ImageMagick;
using PhotoGrouper.Application.Abstractions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PixelFormat = PhotoGrouper.Domain.Common.PixelFormat;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>
/// Decodes Apple's HEIF container, the default capture format on modern iPhones.
/// </summary>
/// <remarks>
/// A separate adapter because OpenCV does not decode HEIC at all. Leaving it out would not
/// produce an error the user could act on; a library imported from a phone would simply appear
/// to contain almost no photos.
///
/// Magick.NET is used rather than LibHeifSharp because it ships its own native binaries in the
/// NuGet package. LibHeifSharp requires a separately built libheif on the machine, which turns
/// a supported format into a deployment problem.
/// </remarks>
public sealed class HeifImageDecoder : IImageDecoder
{
    public bool CanDecode(string path) =>
        SupportedImageFormats.Heif.Contains(Path.GetExtension(path));

    public Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken ct) =>
        Task.Run(() => ReadMetadata(path), ct);

    public Task<DecodedImage?> DecodeAsync(string path, int? maxLongEdge, CancellationToken ct) =>
        Task.Run(() => Decode(path, maxLongEdge), ct);

    private static ImageMetadata? ReadMetadata(string path)
    {
        // Preferred over Magick for metadata: it parses the EXIF block without asking a native
        // library to decode a frame, which for HEIC is expensive.
        if (ExifReader.Read(path) is { } fromExif)
        {
            return fromExif;
        }

        try
        {
            using var image = new MagickImage(path);
            return new ImageMetadata(
                (int)image.Width, (int)image.Height, ExifReader.DefaultOrientation, null, null);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static DecodedImage? Decode(string path, int? maxLongEdge)
    {
        try
        {
            using var image = new MagickImage(path);

            // AutoOrient is Magick's own handling of the orientation tag, including the HEIF
            // container's rotation box, which is not the same thing as EXIF and which this code
            // has no way to read for itself. Once applied, the pixels are upright and the tag is
            // cleared, so nothing downstream may rotate again.
            image.AutoOrient();

            var originalWidth = (int)image.Width;
            var originalHeight = (int)image.Height;

            var scale = OpenCvImageDecoder.ComputeScale(originalWidth, originalHeight, maxLongEdge);
            if (scale < 1f)
            {
                image.Resize(
                    (uint)Math.Max(1, (int)MathF.Round(originalWidth * scale)),
                    (uint)Math.Max(1, (int)MathF.Round(originalHeight * scale)));
            }

            // HEIC is commonly encoded in a wide colour space. Without this the channel values
            // handed to a model trained on sRGB are systematically off.
            image.ColorSpace = ColorSpace.sRGB;
            image.Depth = 8;

            using var pixels = image.GetPixels();
            var bgr = pixels.ToByteArray(PixelMapping.BGR);
            if (bgr is null)
            {
                return null;
            }

            var width = (int)image.Width;
            var height = (int)image.Height;
            var buffer = new ImageBuffer(width, height, width * 3, PixelFormat.Bgr24, bgr);

            return new DecodedImage(buffer, originalWidth, originalHeight, scale);
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
