namespace PhotoGrouper.Application.Ports;

/// <summary>Source of the current time.</summary>
/// <remarks>
/// A port so that tests over scan timestamps and export run history are deterministic
/// rather than dependent on when they happen to run.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
