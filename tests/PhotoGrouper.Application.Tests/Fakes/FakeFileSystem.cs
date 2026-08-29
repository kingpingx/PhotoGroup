using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;

namespace PhotoGrouper.Application.Tests.Fakes;

/// <summary>An in-memory filesystem.</summary>
/// <remarks>
/// Exists so that scan and export behaviour can be tested against conditions that are
/// impractical to arrange for real: an unreachable drive, a disk that fills partway through
/// a copy, a file that vanishes between being planned and being written. Those are exactly
/// the paths where photos could be lost, so they are the ones that most need covering.
/// </remarks>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free space reported for every volume. Set low to exercise the space check.</summary>
    public long FreeSpace { get; set; } = long.MaxValue;

    /// <summary>When set, any copy or move of this path throws, simulating a mid-run failure.</summary>
    public string? FailOperationsOn { get; set; }

    public IReadOnlyDictionary<string, FakeFile> Files => _files;

    public FakeFileSystem AddDirectory(string path)
    {
        _directories.Add(path);
        return this;
    }

    public FakeFileSystem AddFile(string path, long length = 1024, DateTimeOffset? modified = null, string content = "x")
    {
        _files[path] = new FakeFile(length, modified ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), content);

        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            _directories.Add(directory);
            directory = Path.GetDirectoryName(directory);
        }

        return this;
    }

    public void Touch(string path, long length, DateTimeOffset modified) =>
        _files[path] = _files[path] with { Length = length, ModifiedUtc = modified };

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public void CreateDirectory(string path) => _directories.Add(path);

    public IEnumerable<FileEntry> EnumerateFiles(
        string root, bool recursive, IReadOnlySet<string> extensions, CancellationToken ct)
    {
        foreach (var (path, file) in _files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!recursive && !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!extensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            yield return new FileEntry(path, file.Length, file.ModifiedUtc);
        }
    }

    public FileEntry? GetFileInfo(string path) =>
        _files.TryGetValue(path, out var file) ? new FileEntry(path, file.Length, file.ModifiedUtc) : null;

    public Task<ContentHash> ComputeContentHashAsync(string path, CancellationToken ct) =>
        Task.FromResult(new ContentHash(_files[path].Content));

    /// <remarks>
    /// The real rule is a property of the filesystem, so this keeps only the part any adapter must
    /// honour: a name never comes back empty, and never carries a path separator.
    /// </remarks>
    public string ToFolderName(string name)
    {
        var cleaned = new string([.. (name ?? string.Empty)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();

        return cleaned.Length == 0 ? "Unnamed" : cleaned;
    }

    public Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        Guard(source);
        _files[destination] = _files[source];
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        Guard(source);
        _files[destination] = _files[source];
        _files.Remove(source);
        return Task.CompletedTask;
    }

    public void Delete(string path) => _files.Remove(path);

    public long GetAvailableFreeSpace(string path) => FreeSpace;

    public bool AreOnSameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private void Guard(string path)
    {
        if (FailOperationsOn is { } failing && string.Equals(failing, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Simulated failure operating on {path}.");
        }
    }

    public readonly record struct FakeFile(long Length, DateTimeOffset ModifiedUtc, string Content);
}
