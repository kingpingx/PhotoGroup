using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Storage for export runs and the operations that make them up.
/// </summary>
/// <remarks>
/// Not a log. For a move this is the undo journal, and it is the only record of where somebody's
/// photographs went, so it is written before each file is touched rather than after. A row saying
/// a move was planned but not done, found after a crash, is the signal to check that file rather
/// than assume it stayed put.
/// </remarks>
public interface IExportRepository
{
    Task AddRunAsync(ExportRun run, CancellationToken ct);

    Task UpdateRunAsync(ExportRun run, CancellationToken ct);

    Task<ExportRun?> GetRunAsync(ExportRunId id, CancellationToken ct);

    /// <summary>Runs newest first, for showing what has been done and offering to undo it.</summary>
    Task<IReadOnlyList<ExportRun>> GetRecentRunsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Writes the planned operations before any file is touched.
    /// </summary>
    /// <remarks>
    /// The whole plan at once, so that a run interrupted at any point has a complete record of what
    /// it intended. Writing each row as its file is reached would leave an interrupted move with no
    /// record of the work it had not yet started, which is exactly what an undo has to reason about.
    /// </remarks>
    Task AddOpsAsync(IReadOnlyList<ExportOp> ops, CancellationToken ct);

    Task UpdateOpAsync(ExportOp op, CancellationToken ct);

    Task<IReadOnlyList<ExportOp>> GetOpsAsync(ExportRunId runId, CancellationToken ct);
}
