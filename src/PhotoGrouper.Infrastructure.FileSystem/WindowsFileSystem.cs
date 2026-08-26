using System.IO.Hashing;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Infrastructure.FileSystem;

/// <summary>Filesystem access backed by System.IO.</summary>
public sealed class WindowsFileSystem : IFileSystem
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

    public long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
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
