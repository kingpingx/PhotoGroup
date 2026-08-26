using PhotoGrouper.Application.Abstractions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Walks the configured scan roots and brings the photo index up to date with disk.
/// </summary>
/// <remarks>
/// Only discovery and change detection happen here. Decoding, detection and embedding are
/// separate stages that read from the index by state, which is what allows an interrupted
/// scan to resume instead of restarting.
/// </remarks>
public sealed class ScanLibraryUseCase(
    IScanRootRepository scanRoots,
    IPhotoReader photoReader,
    IPhotoWriter photoWriter,
    IFileSystem fileSystem,
    IClock clock)
{
    /// <summary>How many discovered files are accumulated before being written as one batch.</summary>
    private const int BatchSize = 500;

    public async Task<ScanResult> ExecuteAsync(IProgressSink progress, CancellationToken ct)
    {
        var roots = await scanRoots.GetAllAsync(ct).ConfigureAwait(false);
        if (roots.Count == 0)
        {
            return new ScanResult(0, 0, 0, 0);
        }

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;
        var seen = 0;
        var batch = new List<Photo>(BatchSize);

        foreach (var root in roots)
        {
            if (!fileSystem.DirectoryExists(root.Path))
            {
                // A root on a drive that is not currently mounted is not an error. Leaving
                // its photos in the index means they reappear when the drive returns,
                // rather than being deleted and re-detected from scratch.
                skipped++;
                continue;
            }

            progress.Report(new ProgressUpdate("Scanning", seen, null, root.Path));

            foreach (var file in fileSystem.EnumerateFiles(root.Path, root.Recursive, SupportedImageFormats.All, ct))
            {
                ct.ThrowIfCancellationRequested();
                seen++;

                var existing = await photoReader.GetByPathAsync(file.Path, ct).ConfigureAwait(false);

                if (existing is null)
                {
                    batch.Add(new Photo(
                        PhotoId.New(),
                        file.Path,
                        file.Length,
                        file.ModifiedUtc,
                        state: PhotoState.New));
                    added++;
                }
                else if (existing.HasChanged(file.Length, file.ModifiedUtc))
                {
                    // The bytes changed, so everything derived from them is stale. Resetting
                    // to New rather than editing in place sends the file back through the
                    // whole pipeline, which is the only way its faces can be correct.
                    batch.Add(new Photo(
                        existing.Id,
                        file.Path,
                        file.Length,
                        file.ModifiedUtc,
                        state: PhotoState.New));
                    updated++;
                }
                else
                {
                    unchanged++;
                }

                if (batch.Count >= BatchSize)
                {
                    await photoWriter.BulkUpsertAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                    progress.Report(new ProgressUpdate("Scanning", seen, null, file.Path));
                }
            }

            await scanRoots.MarkScannedAsync(root.Id, clock.UtcNow, ct).ConfigureAwait(false);
        }

        if (batch.Count > 0)
        {
            await photoWriter.BulkUpsertAsync(batch, ct).ConfigureAwait(false);
        }

        progress.Report(new ProgressUpdate("Scanning", seen, seen));
        return new ScanResult(added, updated, unchanged, skipped);
    }
}

/// <param name="Added">Files not previously in the index.</param>
/// <param name="Updated">Files whose contents changed and must be reprocessed.</param>
/// <param name="Unchanged">Files already indexed and untouched since.</param>
/// <param name="SkippedRoots">Roots that could not be reached, typically an offline drive.</param>
public readonly record struct ScanResult(int Added, int Updated, int Unchanged, int SkippedRoots);
