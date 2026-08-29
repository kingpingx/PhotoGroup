using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Puts back the files a move export relocated.
/// </summary>
/// <remarks>
/// The reason the journal is written to disk rather than held in memory. A move is the one
/// operation here that leaves somebody's photographs somewhere they did not put them, and the only
/// record of where each one came from is the row written before it was touched.
///
/// A copy is never undone, and the offer is not made: nothing was taken away, so there is nothing
/// to put back, and deleting the copies would be a destructive act dressed up as a correction.
///
/// Undo is itself journalled by marking each row as it succeeds, so an undo interrupted half way
/// can be run again and will only attempt the files it did not reach.
/// </remarks>
public sealed class UndoExportUseCase(
    IExportRepository exports,
    IPhotoWriter index,
    IFileSystem files,
    IClock clock)
{
    public async Task<UndoResult> ExecuteAsync(
        ExportRunId runId, IProgressSink progress, CancellationToken ct)
    {
        var run = await exports.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return UndoResult.Failed("That export is no longer recorded.");
        }

        if (run.Mode == ExportMode.Copy)
        {
            return UndoResult.Failed(
                "That export copied files, so nothing was taken away. Delete the copies yourself "
                + "if you no longer want them.");
        }

        if (run.Status == ExportRunStatus.Undone)
        {
            return UndoResult.Failed("That export has already been put back.");
        }

        var ops = await exports.GetOpsAsync(runId, ct).ConfigureAwait(false);

        // Only the rows that actually moved. A row still marked planned describes a file the run
        // never reached, which is therefore still where it always was.
        var moved = ops.Where(op => op.Status == ExportOpStatus.Done).ToList();

        progress.Report(new ProgressUpdate("Putting back", 0, moved.Count));

        var restored = 0;
        var blocked = 0;

        foreach (var op in moved)
        {
            ct.ThrowIfCancellationRequested();

            // Something is already back at the original path. Refusing rather than overwriting:
            // whatever is there was not put there by this run, and it is not this operation's to
            // destroy.
            if (files.FileExists(op.SourcePath))
            {
                blocked++;
                continue;
            }

            if (!files.FileExists(op.DestinationPath))
            {
                blocked++;
                continue;
            }

            try
            {
                var folder = Path.GetDirectoryName(op.SourcePath);
                if (!string.IsNullOrEmpty(folder) && !files.DirectoryExists(folder))
                {
                    files.CreateDirectory(folder);
                }

                await files.MoveAsync(op.DestinationPath, op.SourcePath, ct).ConfigureAwait(false);

                // The index follows the file back, and only once the file has actually arrived.
                await index.UpdatePathAsync(op.PhotoId, op.SourcePath, ct).ConfigureAwait(false);

                op.MarkUndone();
                await exports.UpdateOpAsync(op, ct).ConfigureAwait(false);

                restored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left as done, so running the undo again will try this file once more rather than
                // treating it as already back.
                blocked++;
            }

            progress.Report(new ProgressUpdate("Putting back", restored + blocked, moved.Count));
        }

        // Marked undone only when everything got home. A partial undo that claimed to be complete
        // would take the offer to finish it off the screen.
        if (blocked == 0)
        {
            run.MarkUndone(clock.UtcNow);
            await exports.UpdateRunAsync(run, ct).ConfigureAwait(false);
        }

        return UndoResult.Succeeded(restored, blocked);
    }
}

/// <param name="Blocked">
/// Files that could not be put back: something is at the original path, the exported file has gone,
/// or it is locked. Left recorded as moved so that running this again retries them.
/// </param>
public readonly record struct UndoResult(bool IsSuccess, int Restored, int Blocked, string? Error)
{
    public static UndoResult Succeeded(int restored, int blocked) =>
        new(true, restored, blocked, null);

    public static UndoResult Failed(string error) => new(false, 0, 0, error);
}
