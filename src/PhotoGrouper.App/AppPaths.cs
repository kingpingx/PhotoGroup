namespace PhotoGrouper.App;

/// <summary>Where the app keeps its database and caches.</summary>
/// <remarks>
/// Everything here is rebuildable from the photo library, so it belongs in LocalApplicationData
/// rather than roaming: the thumbnail cache alone runs to hundreds of megabytes for a large
/// library and has no business following a user between machines.
/// </remarks>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoGrouper");

    public static string DatabaseFile => Path.Combine(Root, "library.db");

    public static string ThumbnailCache => Path.Combine(Root, "thumbs");

    public static string Models => Path.Combine(Root, "models");

    public static string Providers => Path.Combine(Root, "providers");
}
