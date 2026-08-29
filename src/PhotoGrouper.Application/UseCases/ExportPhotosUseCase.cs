using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Writes the library out as folders, one per person.
/// </summary>
/// <remarks>
/// The point at which grouping leaves this application and becomes something any other program can
/// read: a folder called Alice holding every photograph of Alice. Nothing else here produces
/// anything usable outside the app.
///
/// It plans the whole run before touching a single file, and writes that plan down first. Two
/// things follow from that. A move interrupted half way can be put back, because the journal
/// records where every file was going before it went. And a run can refuse before it starts —
/// somewhere without room, a destination inside the library it is reading from — rather than
/// discovering it with half the photographs moved.
///
/// A photograph of two people is written into both their folders. Filing it under only the first
/// would make the folders quietly wrong for everybody else in the picture, and there is no answer
/// to "which of them does this photograph belong to" that is right.
/// </remarks>
public sealed class ExportPhotosUseCase(
    IPersonRepository people,
    IFaceRepository faces,
    IPhotoReader photos,
    IPhotoWriter index,
    IScanRootRepository roots,
    IExportRepository exports,
    IFileSystem files,
    IClock clock)
{
    /// <summary>How a photograph's place in the output is decided.</summary>
    public const string PerPersonFolder = "person/filename";

    public async Task<ExportResult> ExecuteAsync(
        ExportRequest request, IProgressSink progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OutputRoot))
        {
            return ExportResult.Failed("Choose a folder to write into.");
        }

        // Refused before anything is planned. Writing into a scanned folder means the next scan
        // indexes the copies as new photographs, detects faces in them, and produces a second copy
        // of everybody — and for a move it would fight the library's own record of where files are.
        if (await InsideAScanRootAsync(request.OutputRoot, ct).ConfigureAwait(false) is { } root)
        {
            return ExportResult.Failed(
                $"That folder is inside {root}, which this library scans. Choose somewhere outside "
                + "your photo folders, or the exported copies come back as new photographs.");
        }

        progress.Report(new ProgressUpdate("Planning", 0, null));

        // The run's identity is minted here and handed to the planner, so that every operation
        // references the run that is actually recorded. Letting the planner mint its own left the
        // journal pointing at a run that never existed.
        var runId = ExportRunId.New();

        var plan = await PlanAsync(runId, request, ct).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            return ExportResult.Failed("Nobody chosen has any photographs.");
        }

        // Checked once, against the whole plan, rather than discovered on the file that does not
        // fit. A copy that runs out of room half way leaves the user to work out what arrived.
        if (request.Mode == ExportMode.Copy)
        {
            var needed = plan.Sum(op => op.Bytes);
            if (files.GetAvailableFreeSpace(request.OutputRoot) < needed)
            {
                return ExportResult.Failed(
                    $"That folder does not have room for {Megabytes(needed)} of photos.");
            }
        }

        var run = new ExportRun(
            runId, clock.UtcNow, request.OutputRoot,
            PerPersonFolder, request.Mode, request.Source);

        await exports.AddRunAsync(run, ct).ConfigureAwait(false);
        await exports.AddOpsAsync(plan, ct).ConfigureAwait(false);

        var written = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;

        progress.Report(new ProgressUpdate("Writing", 0, plan.Count));

        foreach (var op in plan)
        {
            if (ct.IsCancellationRequested)
            {
                run.Finish(ExportRunStatus.Cancelled, clock.UtcNow);
                await exports.UpdateRunAsync(run, ct).ConfigureAwait(false);

                return ExportResult.Succeeded(
                    run.Id, request.Mode, written, skipped, failed, bytes, request.OutputRoot, true);
            }

            var outcome = await WriteAsync(op, request.Mode, CancellationToken.None).ConfigureAwait(false);

            switch (outcome)
            {
                case ExportOpStatus.Done:
                    written++;
                    bytes += op.Bytes;
                    break;
                case ExportOpStatus.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }

            await exports.UpdateOpAsync(op, CancellationToken.None).ConfigureAwait(false);
            progress.Report(new ProgressUpdate("Writing", written + skipped + failed, plan.Count));
        }

        run.Finish(failed == 0 ? ExportRunStatus.Completed : ExportRunStatus.Failed, clock.UtcNow);
        await exports.UpdateRunAsync(run, CancellationToken.None).ConfigureAwait(false);

        return ExportResult.Succeeded(
            run.Id, request.Mode, written, skipped, failed, bytes, request.OutputRoot, false);
    }

    /// <summary>
    /// Where every photograph is going, decided before any of them moves.
    /// </summary>
    /// <remarks>
    /// Destination names are made unique within the run as it is built, because two people's
    /// photographs frequently share a file name and, for a move especially, a collision that
    /// overwrote would destroy the very file being preserved.
    /// </remarks>
    private async Task<List<ExportOp>> PlanAsync(
        ExportRunId runId, ExportRequest request, CancellationToken ct)
    {
        var chosen = request.Source == ExportSource.EveryNamedPerson
            ? await people.GetAllAsync(ct).ConfigureAwait(false)
            : [.. (await people.GetAllAsync(ct).ConfigureAwait(false))
                .Where(person => request.People.Contains(person.Id))];

        var plan = new List<ExportOp>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in chosen)
        {
            ct.ThrowIfCancellationRequested();

            var folder = Path.Combine(request.OutputRoot, files.ToFolderName(person.Name.Value));
            var theirs = await faces
                .GetByPersonAsync(person.Id, request.DetectorId, ct)
                .ConfigureAwait(false);

            foreach (var photoId in theirs.Select(face => face.PhotoId).Distinct())
            {
                var photo = await photos.GetByIdAsync(photoId, ct).ConfigureAwait(false);
                if (photo is null)
                {
                    continue;
                }

                var destination = UniqueDestination(folder, photo.Path, taken);
                taken.Add(destination);

                plan.Add(new ExportOp(
                    ExportOpId.New(), runId, photo.Id, person.Id,
                    photo.Path, destination, request.Mode, bytes: photo.FileSize));
            }
        }

        // A move writes each file once. Filing one photograph into two people's folders is right
        // for a copy and impossible for a move, so a move keeps the first folder it was planned
        // into rather than pretending to be in both.
        return request.Mode == ExportMode.Move
            ? [.. plan.GroupBy(op => op.PhotoId).Select(group => group.First())]
            : plan;
    }

    private async Task<ExportOpStatus> WriteAsync(ExportOp op, ExportMode mode, CancellationToken ct)
    {
        if (!files.FileExists(op.SourcePath))
        {
            op.Fail("The file is no longer there.");
            return ExportOpStatus.Failed;
        }

        // Already there from an earlier run of the same export. Skipped rather than overwritten,
        // so running an export twice is safe and cheap instead of rewriting the whole library.
        if (files.FileExists(op.DestinationPath))
        {
            op.Skip("Already exported.");
            return ExportOpStatus.Skipped;
        }

        try
        {
            var folder = Path.GetDirectoryName(op.DestinationPath);
            if (!string.IsNullOrEmpty(folder) && !files.DirectoryExists(folder))
            {
                files.CreateDirectory(folder);
            }

            if (mode == ExportMode.Copy)
            {
                await files.CopyAsync(op.SourcePath, op.DestinationPath, ct).ConfigureAwait(false);
            }
            else
            {
                await files.MoveAsync(op.SourcePath, op.DestinationPath, ct).ConfigureAwait(false);

                // The library's record follows the file. Without this the photograph is still
                // indexed at a path holding nothing, so every thumbnail and every face on it breaks
                // at once — and the next scan re-indexes it at its new home as a stranger.
                await index.UpdatePathAsync(op.PhotoId, op.DestinationPath, ct).ConfigureAwait(false);
            }

            op.Succeed(op.Bytes);
            return ExportOpStatus.Done;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            op.Fail(e.Message);
            return ExportOpStatus.Failed;
        }
    }

    private string UniqueDestination(string folder, string sourcePath, HashSet<string> taken)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var candidate = Path.Combine(folder, name + extension);

        var suffix = 2;
        while (taken.Contains(candidate))
        {
            candidate = Path.Combine(folder, $"{name} ({suffix++}){extension}");
        }

        return candidate;
    }

    private async Task<string?> InsideAScanRootAsync(string folder, CancellationToken ct)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        foreach (var root in await roots.GetAllAsync(ct).ConfigureAwait(false))
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));

            // Segment-wise, so a folder named "PicturesExport" is not judged to be inside
            // "Pictures".
            if (full.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return root.Path;
            }
        }

        return null;
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:N0} MB";
}

/// <param name="People">Who to write out, when the source is a chosen few.</param>
public sealed record ExportRequest(
    string OutputRoot,
    ExportMode Mode,
    ExportSource Source,
    IReadOnlyList<PersonId> People,
    string DetectorId);

/// <param name="Skipped">Photographs already present in the destination from an earlier run.</param>
public readonly record struct ExportResult(
    bool IsSuccess,
    ExportRunId RunId,
    ExportMode Mode,
    int Written,
    int Skipped,
    int FailedCount,
    long Bytes,
    string? Folder,
    bool Cancelled,
    string? Error)
{
    /// <summary>The count is named apart from the factory below, which shares the word.</summary>
    public static ExportResult Succeeded(
        ExportRunId id, ExportMode mode, int written, int skipped, int failed,
        long bytes, string folder, bool cancelled) =>
        new(true, id, mode, written, skipped, failed, bytes, folder, cancelled, null);

    public static ExportResult Failed(string error) =>
        new(false, default, default, 0, 0, 0, 0, null, false, error);
}
