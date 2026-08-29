using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Moves the duplicates a user has chosen into a folder of their own.
/// </summary>
/// <remarks>
/// Moves rather than deletes, and that is the whole design. This application's promise is that it
/// leaves originals where they are; the one place it may move a file is where somebody has looked
/// at two pictures and said to remove one, and even then the file goes somewhere they can open it.
/// Deleting would make a misjudged threshold unrecoverable, and thresholds are judgements.
///
/// Nothing is moved until every check has passed. A run that moved four files and then found the
/// fifth already gone would leave the library half-corrected with no record of where it stopped.
/// </remarks>
public sealed class QuarantineDuplicatesUseCase(
    IPhotoReader photos,
    IPhotoWriter index,
    IScanRootRepository roots,
    IFileSystem files)
{
    public async Task<QuarantineResult> ExecuteAsync(
        IReadOnlyList<PhotoId> photoIds,
        string destinationFolder,
        CancellationToken ct)
    {
        if (photoIds.Count == 0)
        {
            return QuarantineResult.Failed("Nothing was selected.");
        }

        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return QuarantineResult.Failed("Choose a folder to move the duplicates into.");
        }

        // A destination inside a scanned folder undoes the whole operation at the next scan: the
        // files are indexed again from their new home, reappear as duplicates of each other, and
        // the user is offered the same decision they already made. Worth refusing rather than
        // explaining, because the failure only shows up much later and looks like a bug in
        // scanning.
        if (await IsInsideAScanRootAsync(destinationFolder, ct).ConfigureAwait(false) is { } root)
        {
            return QuarantineResult.Failed(
                $"That folder is inside {root}, which is scanned. The duplicates would be found "
                + "again on the next scan. Choose somewhere outside your photo folders.");
        }

        var planned = new List<(PhotoId Id, string From, string To, long Size)>(photoIds.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        long bytes = 0;

        foreach (var id in photoIds)
        {
            ct.ThrowIfCancellationRequested();

            var photo = await photos.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (photo is null)
            {
                continue;
            }

            // A file that has already gone is not a failure. The user may have moved it themselves
            // between finding the duplicates and confirming them, and the right response is to drop
            // it from the index, which is what they asked for anyway.
            if (!files.FileExists(photo.Path))
            {
                await index.RemoveAsync(id, ct).ConfigureAwait(false);
                missing++;
                continue;
            }

            var destination = UniqueDestination(destinationFolder, photo.Path, taken);
            taken.Add(destination);
            planned.Add((id, photo.Path, destination, photo.FileSize));
            bytes += photo.FileSize;
        }

        if (planned.Count == 0)
        {
            return missing > 0
                ? QuarantineResult.Succeeded(0, missing, 0, destinationFolder)
                : QuarantineResult.Failed("Those photos are no longer in the library.");
        }

        // Checked before the first move rather than discovered during it. A move that runs out of
        // room part way leaves files split across two folders, which is exactly the state somebody
        // trying to tidy their library does not want to be left in.
        if (!files.DirectoryExists(destinationFolder))
        {
            files.CreateDirectory(destinationFolder);
        }

        if (!files.AreOnSameVolume(planned[0].From, destinationFolder)
            && files.GetAvailableFreeSpace(destinationFolder) < bytes)
        {
            return QuarantineResult.Failed(
                $"That folder does not have room for {Megabytes(bytes)} of photos.");
        }

        var moved = 0;
        long recovered = 0;

        foreach (var (id, from, to, size) in planned)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await files.MoveAsync(from, to, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The index still points at a file that is still there, so this photograph is
                // exactly as it was. Reported rather than retried: a locked or read-only file needs
                // the user, not another attempt.
                continue;
            }

            // Removed from the index only once the file has actually moved. The other order loses
            // the photograph from the library while leaving it on disk, which is the one outcome
            // nothing here can put right.
            await index.RemoveAsync(id, ct).ConfigureAwait(false);

            moved++;

            // Summed from what moved, not from what was planned. A file that would not move is
            // still on the disk, and counting it here would report space that was never freed.
            recovered += size;
        }

        return QuarantineResult.Succeeded(moved, missing, recovered, destinationFolder);
    }

    /// <summary>The scan root containing this folder, or null when it is outside all of them.</summary>
    private async Task<string?> IsInsideAScanRootAsync(string folder, CancellationToken ct)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        foreach (var root in await roots.GetAllAsync(ct).ConfigureAwait(false))
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));

            // Compared segment-wise rather than by a plain prefix test, so that a folder named
            // "PicturesBackup" is not judged to be inside "Pictures".
            if (full.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return root.Path;
            }
        }

        return null;
    }

    /// <summary>
    /// A path in the destination that is not already spoken for.
    /// </summary>
    /// <remarks>
    /// Duplicates very often share a name — that is how they came to be duplicates — so collisions
    /// are the normal case rather than the exception. A number is appended rather than overwriting,
    /// because overwriting one duplicate with another destroys the very file this is preserving.
    /// </remarks>
    private string UniqueDestination(string folder, string sourcePath, HashSet<string> taken)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var candidate = Path.Combine(folder, name + extension);

        var suffix = 2;
        while (taken.Contains(candidate) || files.FileExists(candidate))
        {
            candidate = Path.Combine(folder, $"{name} ({suffix++}){extension}");
        }

        return candidate;
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:N0} MB";
}

/// <param name="Moved">Files moved into the folder and dropped from the library.</param>
/// <param name="AlreadyGone">Files that had already left their folder, dropped from the library.</param>
/// <param name="BytesRecovered">How much space the move freed in the original folders.</param>
public readonly record struct QuarantineResult(
    bool IsSuccess, int Moved, int AlreadyGone, long BytesRecovered, string? Folder, string? Error)
{
    public static QuarantineResult Succeeded(int moved, int alreadyGone, long bytes, string folder) =>
        new(true, moved, alreadyGone, bytes, folder, null);

    public static QuarantineResult Failed(string error) => new(false, 0, 0, 0, null, error);
}
