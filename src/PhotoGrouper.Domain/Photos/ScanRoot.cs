using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.Photos;

/// <summary>A folder the library indexes.</summary>
public sealed class ScanRoot
{
    public ScanRoot(
        ScanRootId id,
        string path,
        bool recursive = true,
        bool isImplicit = false,
        DateTimeOffset? lastScanUtc = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));

        Id = id;
        Path = path;
        Recursive = recursive;
        IsImplicit = isImplicit;
        LastScanUtc = lastScanUtc;
    }

    public ScanRootId Id { get; }

    public string Path { get; }

    public bool Recursive { get; }

    /// <summary>
    /// True when the app added this root itself rather than the user choosing it.
    /// </summary>
    /// <remarks>
    /// A move export relocates files out of their original roots. Registering the export
    /// destination automatically keeps those photos tracked; without it the next rescan
    /// would see them as deleted.
    /// </remarks>
    public bool IsImplicit { get; }

    public DateTimeOffset? LastScanUtc { get; }
}
