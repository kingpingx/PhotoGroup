using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>Adds and removes the folders the library indexes.</summary>
public sealed class ManageScanRootsUseCase(IScanRootRepository scanRoots, IFileSystem fileSystem)
{
    public Task<IReadOnlyList<ScanRoot>> ListAsync(CancellationToken ct) => scanRoots.GetAllAsync(ct);

    public async Task<AddScanRootResult> AddAsync(string path, bool recursive, CancellationToken ct)
    {
        if (!fileSystem.DirectoryExists(path))
        {
            return AddScanRootResult.NotFound;
        }

        var normalised = Normalise(path);

        if (await scanRoots.GetByPathAsync(normalised, ct).ConfigureAwait(false) is not null)
        {
            return AddScanRootResult.AlreadyPresent;
        }

        // A folder already covered by another root would have every one of its files enumerated
        // twice on each scan. The photographs themselves are keyed by path so nothing is duplicated
        // in the library, but the work is done twice and the folder list implies two separate
        // sources where there is one.
        var existing = await scanRoots.GetAllAsync(ct).ConfigureAwait(false);

        if (existing.Any(root => Contains(root.Path, normalised)))
        {
            return AddScanRootResult.AlreadyCovered;
        }

        // The reverse: adding a parent of folders already listed. The parent is kept, since it is
        // what the user asked for, and the redundant children are dropped.
        foreach (var nested in existing.Where(root => Contains(normalised, root.Path)))
        {
            await scanRoots.RemoveAsync(nested.Id, ct).ConfigureAwait(false);
        }

        await scanRoots.AddAsync(new ScanRoot(ScanRootId.New(), normalised, recursive), ct)
            .ConfigureAwait(false);

        return AddScanRootResult.Added;
    }

    /// <summary>
    /// Puts a folder path into one canonical form.
    /// </summary>
    /// <remarks>
    /// Without this the same folder can be added more than once, because the duplicate check
    /// compares strings: a trailing separator, a relative segment, or a short name all describe the
    /// same directory while comparing unequal, and each spelling gets its own root.
    /// </remarks>
    internal static string Normalise(string path)
    {
        var full = Path.GetFullPath(path);

        // A drive root is exactly the case where the trailing separator must stay: "D:" is not the
        // same thing as "D:\" to the filesystem.
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>True when <paramref name="candidate"/> lies inside <paramref name="parent"/>.</summary>
    /// <remarks>
    /// Compared segment by segment rather than by string prefix, so that "D:\photos-old" is not
    /// mistaken for a child of "D:\photos".
    /// </remarks>
    private static bool Contains(string parent, string candidate)
    {
        var normalisedParent = Normalise(parent);
        var normalisedCandidate = Normalise(candidate);

        if (string.Equals(normalisedParent, normalisedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalisedCandidate.StartsWith(
            normalisedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public Task RemoveAsync(ScanRootId id, CancellationToken ct) => scanRoots.RemoveAsync(id, ct);
}

public enum AddScanRootResult
{
    Added,

    /// <summary>This exact folder is already listed.</summary>
    AlreadyPresent,

    /// <summary>A folder already listed contains this one, so it is scanned already.</summary>
    AlreadyCovered,

    NotFound,
}
