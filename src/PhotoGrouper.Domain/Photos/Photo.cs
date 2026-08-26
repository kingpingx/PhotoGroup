using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.Photos;

/// <summary>
/// One image file tracked by the library.
/// </summary>
/// <remarks>
/// Every field here is either expensive to recompute or impossible to recompute.
/// Path, length and modified time form the incremental scan key that lets a rescan skip
/// unchanged files. Dimensions and orientation let the UI map face boxes onto a
/// thumbnail without decoding the original again. EXIF capture data is cheap to parse
/// but requires opening the file, which is the part worth avoiding.
/// </remarks>
public sealed class Photo
{
    public Photo(
        PhotoId id,
        string path,
        long fileSize,
        DateTimeOffset modifiedUtc,
        ContentHash? contentHash = null,
        int? width = null,
        int? height = null,
        int orientation = 1,
        DateTimeOffset? takenUtc = null,
        string? camera = null,
        PhotoState state = PhotoState.New,
        DateTimeOffset? indexedUtc = null,
        string? error = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
        if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));

        Id = id;
        Path = path;
        FileSize = fileSize;
        ModifiedUtc = modifiedUtc;
        ContentHash = contentHash;
        Width = width;
        Height = height;
        Orientation = orientation;
        TakenUtc = takenUtc;
        Camera = camera;
        State = state;
        IndexedUtc = indexedUtc;
        Error = error;
    }

    public PhotoId Id { get; }

    /// <summary>Absolute path to the file. Rewritten when a move export relocates it.</summary>
    public string Path { get; private set; }

    public long FileSize { get; }

    public DateTimeOffset ModifiedUtc { get; }

    public ContentHash? ContentHash { get; }

    public int? Width { get; }

    public int? Height { get; }

    /// <summary>
    /// EXIF orientation tag, 1 through 8.
    /// </summary>
    /// <remarks>
    /// Must be applied before face detection runs. A photo shot in portrait on a phone is
    /// usually stored in landscape with an orientation tag, and detecting on the unrotated
    /// pixels puts every bounding box in the wrong place.
    /// </remarks>
    public int Orientation { get; }

    public DateTimeOffset? TakenUtc { get; }

    public string? Camera { get; }

    public PhotoState State { get; private set; }

    public DateTimeOffset? IndexedUtc { get; }

    public string? Error { get; }

    /// <summary>True when the file on disk differs from what was indexed and must be reprocessed.</summary>
    public bool HasChanged(long fileSize, DateTimeOffset modifiedUtc) =>
        FileSize != fileSize || ModifiedUtc != modifiedUtc;

    public void AdvanceTo(PhotoState state) => State = state;

    /// <summary>Records that the file now lives at a different path, after a move export.</summary>
    public void RelocateTo(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) throw new ArgumentException("Path is required.", nameof(newPath));
        Path = newPath;
    }
}
