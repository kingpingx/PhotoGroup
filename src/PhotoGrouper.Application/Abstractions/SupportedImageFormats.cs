namespace PhotoGrouper.Application.Abstractions;

/// <summary>File extensions the library will index.</summary>
public static class SupportedImageFormats
{
    /// <summary>Formats decodable without additional native dependencies.</summary>
    public static readonly IReadOnlySet<string> Standard =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".bmp", ".tif", ".tiff",
        };

    /// <summary>
    /// Apple's HEIF container, the default capture format on modern iPhones.
    /// </summary>
    /// <remarks>
    /// Kept separate because it needs a decoder the standard set does not. Omitting HEIC
    /// from a phone-sourced library silently skips a large share of the photos, which is
    /// worse than failing loudly, so it is supported from the start.
    /// </remarks>
    public static readonly IReadOnlySet<string> Heif =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>Every extension the scanner picks up.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Standard.Concat(Heif), StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) =>
        All.Contains(Path.GetExtension(path));
}
