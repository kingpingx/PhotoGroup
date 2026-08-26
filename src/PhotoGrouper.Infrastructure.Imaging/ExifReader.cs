using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoGrouper.Application.Ports;
using Directory = MetadataExtractor.Directory;

namespace PhotoGrouper.Infrastructure.Imaging;

/// <summary>Reads EXIF without decoding pixels.</summary>
internal static class ExifReader
{
    /// <summary>The orientation assumed when a file carries no tag: upright, no transform.</summary>
    public const int DefaultOrientation = 1;

    public static ImageMetadata? Read(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);

            var (width, height) = ReadDimensions(directories);
            if (width is null || height is null)
            {
                return null;
            }

            var orientation = ReadOrientation(directories);

            // Dimensions in the file describe the stored pixels. A photo tagged for a quarter
            // turn is stored landscape and displayed portrait, so the upright dimensions are
            // the stored ones swapped. Reporting the stored pair would make every face box
            // computed against the upright image fall outside the photo's declared bounds.
            var (uprightWidth, uprightHeight) = RequiresTranspose(orientation)
                ? (height.Value, width.Value)
                : (width.Value, height.Value);

            return new ImageMetadata(
                uprightWidth,
                uprightHeight,
                orientation,
                ReadTakenUtc(directories),
                ReadCamera(directories));
        }
        catch (Exception e) when (e is IOException or ImageProcessingException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Orientation tag, 1 to 8, defaulting to upright when absent or nonsensical.</summary>
    public static int ReadOrientation(IReadOnlyList<Directory> directories)
    {
        foreach (var ifd0 in directories.OfType<ExifIfd0Directory>())
        {
            if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value) && value is >= 1 and <= 8)
            {
                return value;
            }
        }

        return DefaultOrientation;
    }

    /// <summary>True when the orientation exchanges width and height.</summary>
    public static bool RequiresTranspose(int orientation) => orientation is 5 or 6 or 7 or 8;

    private static (int? Width, int? Height) ReadDimensions(IReadOnlyList<Directory> directories)
    {
        foreach (var directory in directories)
        {
            var width = TryReadAny(directory, "Image Width", "Exif Image Width");
            var height = TryReadAny(directory, "Image Height", "Exif Image Height");

            if (width is not null && height is not null)
            {
                return (width, height);
            }
        }

        return (null, null);

        static int? TryReadAny(Directory directory, params string[] names)
        {
            foreach (var tag in directory.Tags)
            {
                if (names.Contains(tag.Name, StringComparer.Ordinal)
                    && directory.TryGetInt32(tag.Type, out var value)
                    && value > 0)
                {
                    return value;
                }
            }

            return null;
        }
    }

    private static DateTimeOffset? ReadTakenUtc(IReadOnlyList<Directory> directories)
    {
        foreach (var subIfd in directories.OfType<ExifSubIfdDirectory>())
        {
            // Original is when the shutter fired; Digitized is when it was written to a card.
            // They differ for scans and imports, and the former is what a user means by "taken".
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(original, DateTimeKind.Utc), TimeSpan.Zero);
            }

            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(digitized, DateTimeKind.Utc), TimeSpan.Zero);
            }
        }

        return null;
    }

    private static string? ReadCamera(IReadOnlyList<Directory> directories)
    {
        foreach (var ifd0 in directories.OfType<ExifIfd0Directory>())
        {
            var make = ifd0.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            var model = ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();

            if (string.IsNullOrEmpty(model))
            {
                continue;
            }

            // Manufacturers frequently repeat the make inside the model ("NIKON D850"), so
            // concatenating unconditionally produces "NIKON CORPORATION NIKON D850".
            return string.IsNullOrEmpty(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                ? model
                : $"{make} {model}";
        }

        return null;
    }
}
