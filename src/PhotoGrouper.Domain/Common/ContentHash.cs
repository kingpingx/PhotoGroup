namespace PhotoGrouper.Domain.Common;

/// <summary>
/// A cheap content fingerprint for a photo file.
/// </summary>
/// <remarks>
/// Deliberately not a full-file cryptographic digest. Hashing 50k files of several
/// megabytes each would add many minutes to every scan, and this value is only used to
/// notice the same photo sitting at two paths and to verify that a copy or move landed
/// intact. The composition of file length plus the head and tail of the content is
/// sufficient for both, since a truncated or partially written file changes length or
/// tail, and two distinct photos sharing length, first 64KB and last 64KB is not a
/// case that arises outside of deliberate construction.
/// </remarks>
public readonly record struct ContentHash(string Value)
{
    /// <summary>The number of bytes sampled from each of the head and tail of a file.</summary>
    public const int SampleBytes = 64 * 1024;

    public static ContentHash Parse(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Content hash cannot be empty.", nameof(value))
            : new ContentHash(value);

    public override string ToString() => Value;
}
