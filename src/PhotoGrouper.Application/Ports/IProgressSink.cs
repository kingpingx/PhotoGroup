namespace PhotoGrouper.Application.Ports;

/// <summary>Reports progress of a long-running background operation.</summary>
public interface IProgressSink
{
    void Report(ProgressUpdate update);
}

/// <summary>A snapshot of a running operation, suitable for direct display.</summary>
/// <param name="Stage">Which pipeline stage is running, for example "Scanning" or "Detecting".</param>
/// <param name="Completed">Items finished so far.</param>
/// <param name="Total">Total items, or null while still being discovered.</param>
/// <param name="Detail">Optional current item, typically a file path.</param>
public readonly record struct ProgressUpdate(string Stage, int Completed, int? Total, string? Detail = null)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Completed / Total.Value, 0, 1) : null;
}
