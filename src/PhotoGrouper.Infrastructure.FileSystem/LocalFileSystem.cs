using System.IO.Hashing;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Infrastructure.FileSystem;

/// <summary>Filesystem access backed by System.IO.</summary>
/// <remarks>
/// Named for the local machine rather than for Windows: every call here is portable, and the
/// previous name implied a platform tie that never existed in the code.
/// </remarks>
public sealed class LocalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <remarks>
    /// Enumeration must not abort partway through a library because one folder is
    /// unreadable. IgnoreInaccessible skips permission failures, and the manual
    /// try/catch covers the rest, so a single bad directory costs its own contents
    /// rather than every file discovered after it.
    /// </remarks>
    public IEnumerable<FileEntry> EnumerateFiles(
        string root,
        bool recursive,
        IReadOnlySet<string> extensions,
        CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", options).GetEnumerator();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                string path;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    path = enumerator.Current;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!extensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                if (GetFileInfo(path) is { } entry)
                {
                    yield return entry;
                }
            }
        }
    }

    public FileEntry? GetFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileEntry(info.FullName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <remarks>
    /// Samples the head and tail rather than digesting the whole file. Reading every byte
    /// of a 50k photo library would add many minutes to a scan for a value that only needs
    /// to distinguish files and confirm a copy landed intact.
    /// </remarks>
    public async Task<ContentHash> ComputeContentHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        var hash = new XxHash128();
        var length = stream.Length;

        hash.Append(BitConverter.GetBytes(length));

        var buffer = new byte[ContentHash.SampleBytes];

        var head = await ReadUpToAsync(stream, buffer, ct).ConfigureAwait(false);
        hash.Append(buffer.AsSpan(0, head));

        if (length > ContentHash.SampleBytes)
        {
            stream.Seek(Math.Max(0, length - ContentHash.SampleBytes), SeekOrigin.Begin);
            var tail = await ReadUpToAsync(stream, buffer, ct).ConfigureAwait(false);
            hash.Append(buffer.AsSpan(0, tail));
        }

        return new ContentHash(Convert.ToHexString(hash.GetCurrentHash()));
    }

    /// <summary>
    /// Names reserved by Windows whatever extension follows them.
    /// </summary>
    /// <remarks>
    /// Creating any of these fails, or worse, silently addresses a device. They are meaningless on
    /// other platforms but harmless there, and a library carried between machines should produce
    /// the same folders on both.
    /// </remarks>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public string ToFolderName(string name)
    {
        var cleaned = new string([.. (name ?? string.Empty)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);

        // Windows silently strips a trailing dot or space from a directory name, which turns two
        // people called "Sam" and "Sam." into one folder and files one of them under the other.
        cleaned = cleaned.Trim().TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            return "Unnamed";
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned)))
        {
            cleaned = "_" + cleaned;
        }

        // Bounded well inside the path limit, because this is one segment of a path that also has
        // a root and a file name to fit.
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd('.', ' ') : cleaned;
    }

    public async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        EnsureParentDirectory(destination);

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// File.Move is an atomic rename when both paths share a volume and a copy-then-delete
    /// otherwise. The distinction matters to the export pipeline, which must verify the
    /// destination before deleting a source across volumes; AreOnSameVolume lets it decide
    /// which path it is on.
    /// </remarks>
    public Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureParentDirectory(destination);
        File.Move(source, destination, overwrite: false);
        return Task.CompletedTask;
    }

    public void Delete(string path) => File.Delete(path);

    /// <remarks>
    /// DriveInfo reports the mounted volume on every platform, so this needs no special case; a
    /// path root is a drive letter on Windows and the mount point on Unix.
    /// </remarks>
    public long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));

        try
        {
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unmounted or unusual volume should not stop an export from being planned.
            return 0;
        }
    }

    public bool AreOnSameVolume(string a, string b)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(a));
        var rootB = Path.GetPathRoot(Path.GetFullPath(b));
        return !string.IsNullOrEmpty(rootA)
               && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
