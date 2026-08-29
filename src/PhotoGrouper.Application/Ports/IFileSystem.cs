using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Application.Ports;

/// <summary>Everything the application needs from the filesystem.</summary>
/// <remarks>
/// A port rather than direct System.IO calls because the export pipeline has to be tested
/// against conditions that are impractical to arrange for real: a disk that fills up
/// midway, a file that vanishes between planning and copying, a verification mismatch.
/// Those tests are the ones protecting against data loss, so they cannot be skipped for
/// want of a seam.
/// </remarks>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    /// <summary>Enumerates files beneath a folder, skipping anything unreadable rather than throwing.</summary>
    IEnumerable<FileEntry> EnumerateFiles(string root, bool recursive, IReadOnlySet<string> extensions, CancellationToken ct);

    FileEntry? GetFileInfo(string path);

    Task<ContentHash> ComputeContentHashAsync(string path, CancellationToken ct);

    Task CopyAsync(string source, string destination, CancellationToken ct);

    /// <summary>
    /// Turns a person's name into something that can be a folder.
    /// </summary>
    /// <remarks>
    /// Here rather than on the name itself, deliberately. What may appear in a folder name is a
    /// property of the filesystem, not of a person: NTFS forbids a different set of characters from
    /// ext4, and reserves whole words such as CON and NUL that mean nothing anywhere else. Putting
    /// those rules on the domain type would move a platform detail into the innermost layer.
    ///
    /// Never returns empty, because a name made entirely of forbidden characters is still somebody.
    /// </remarks>
    string ToFolderName(string name);

    /// <summary>Moves a file, using an atomic rename when both paths are on one volume.</summary>
    Task MoveAsync(string source, string destination, CancellationToken ct);

    void Delete(string path);

    /// <summary>Free bytes on the volume containing <paramref name="path"/>.</summary>
    long GetAvailableFreeSpace(string path);

    /// <summary>True when both paths sit on the same volume, so a move can be a rename.</summary>
    bool AreOnSameVolume(string a, string b);
}

/// <summary>A file as discovered on disk.</summary>
public readonly record struct FileEntry(string Path, long Length, DateTimeOffset ModifiedUtc);
