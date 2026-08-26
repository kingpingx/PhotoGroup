using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.FileSystem;

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
