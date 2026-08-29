using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.Exporting;

/// <summary>Whether an export leaves the originals where they are.</summary>
/// <remarks>
/// The difference between the two is the whole reason this is recorded rather than simply done.
/// A copy that goes wrong costs disk space; a move that goes wrong loses somebody's photographs,
/// so a move is journalled operation by operation and can be undone.
/// </remarks>
public enum ExportMode
{
    /// <summary>The original stays where it is. Nothing can be lost.</summary>
    Copy = 0,

    /// <summary>The original is relocated, and the library's record of it follows.</summary>
    Move = 1,
}

/// <summary>Which photographs an export covers.</summary>
public enum ExportSource
{
    /// <summary>Everybody who has been named.</summary>
    EveryNamedPerson = 0,

    /// <summary>Only the people chosen for this run.</summary>
    ChosenPeople = 1,
}

/// <summary>How far a run got.</summary>
public enum ExportRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Undone = 4,
}

/// <summary>What happened to one photograph.</summary>
public enum ExportOpStatus
{
    Planned = 0,
    Done = 1,
    Failed = 2,
    Skipped = 3,
    Undone = 4,
}

/// <summary>
/// One attempt at writing part of the library out into folders.
/// </summary>
/// <remarks>
/// Persisted rather than held for the duration of the run, because it doubles as the undo journal
/// for a move: a crash part way through a move is exactly when the record of what went where
/// matters most, and it is exactly the moment an in-memory record is gone.
/// </remarks>
public sealed class ExportRun
{
    public ExportRun(
        ExportRunId id,
        DateTimeOffset startedUtc,
        string outputRoot,
        string pattern,
        ExportMode mode,
        ExportSource source,
        ExportRunStatus status = ExportRunStatus.Running,
        DateTimeOffset? finishedUtc = null,
        DateTimeOffset? undoneUtc = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("An export needs somewhere to write to.", nameof(outputRoot));
        }

        Id = id;
        StartedUtc = startedUtc;
        OutputRoot = outputRoot;
        Pattern = pattern;
        Mode = mode;
        Source = source;
        Status = status;
        FinishedUtc = finishedUtc;
        UndoneUtc = undoneUtc;
    }

    public ExportRunId Id { get; }

    public DateTimeOffset StartedUtc { get; }

    public string OutputRoot { get; }

    /// <summary>How a photograph's place in the output is decided, such as one folder per person.</summary>
    public string Pattern { get; }

    public ExportMode Mode { get; }

    public ExportSource Source { get; }

    public ExportRunStatus Status { get; private set; }

    public DateTimeOffset? FinishedUtc { get; private set; }

    public DateTimeOffset? UndoneUtc { get; private set; }

    /// <summary>True when this run moved files and has not yet been put back.</summary>
    public bool CanBeUndone =>
        Mode == ExportMode.Move && Status is ExportRunStatus.Completed or ExportRunStatus.Failed;

    public void Finish(ExportRunStatus status, DateTimeOffset whenUtc)
    {
        Status = status;
        FinishedUtc = whenUtc;
    }

    public void MarkUndone(DateTimeOffset whenUtc)
    {
        Status = ExportRunStatus.Undone;
        UndoneUtc = whenUtc;
    }
}

/// <summary>
/// One photograph's journey in one run.
/// </summary>
/// <remarks>
/// Written before the file is touched and updated after, so a row saying "planned" after a crash
/// means the file may or may not have moved and must be checked, while "done" means it certainly
/// did. That distinction is what makes an undo safe to run against an interrupted export.
/// </remarks>
public sealed class ExportOp(
    ExportOpId id,
    ExportRunId runId,
    PhotoId photoId,
    PersonId? personId,
    string sourcePath,
    string destinationPath,
    ExportMode operation,
    ExportOpStatus status = ExportOpStatus.Planned,
    long bytes = 0,
    string? error = null)
{
    public ExportOpId Id { get; } = id;

    public ExportRunId RunId { get; } = runId;

    public PhotoId PhotoId { get; } = photoId;

    /// <summary>Whose folder this copy went into, or null for a photograph filed under nobody.</summary>
    public PersonId? PersonId { get; } = personId;

    public string SourcePath { get; } = sourcePath;

    public string DestinationPath { get; } = destinationPath;

    public ExportMode Operation { get; } = operation;

    public ExportOpStatus Status { get; private set; } = status;

    public long Bytes { get; private set; } = bytes;

    public string? Error { get; private set; } = error;

    public void Succeed(long bytes)
    {
        Status = ExportOpStatus.Done;
        Bytes = bytes;
        Error = null;
    }

    public void Fail(string error)
    {
        Status = ExportOpStatus.Failed;
        Error = error;
    }

    /// <summary>Nothing to do: the destination already holds this photograph.</summary>
    public void Skip(string reason)
    {
        Status = ExportOpStatus.Skipped;
        Error = reason;
    }

    public void MarkUndone()
    {
        Status = ExportOpStatus.Undone;
        Error = null;
    }
}
