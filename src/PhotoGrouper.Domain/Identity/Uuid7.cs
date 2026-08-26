namespace PhotoGrouper.Domain.Identity;

/// <summary>
/// Time-ordered UUID (version 7) generation.
/// </summary>
/// <remarks>
/// Ids are assigned by the application rather than the store so that identity is
/// portable across storage backends: SQLite has no ObjectId and MongoDB has no
/// autoincrement rowid, and neither concept survives a backend swap. Version 7 is
/// used instead of version 4 because the leading 48 bits are a millisecond timestamp,
/// which keeps newly inserted keys clustered at the end of a B-tree index in any store.
///
/// .NET 9 provides Guid.CreateVersion7(); this project targets .NET 8, so it is
/// implemented here. Remove this type if the project ever moves to .NET 9+.
/// </remarks>
public static class Uuid7
{
    private static readonly object Gate = new();
    private static long _lastMs;
    private static ushort _counter;

    /// <summary>
    /// Creates a new version 7 UUID stamped with the current time.
    /// </summary>
    /// <remarks>
    /// Monotonic: ids from this method always sort in the order they were issued, even when
    /// several are created within the same millisecond or the system clock steps backwards.
    /// </remarks>
    public static Guid NewGuid() => NewMonotonic(DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a version 7 UUID stamped with exactly <paramref name="timestamp"/>.
    /// </summary>
    /// <remarks>
    /// Honours the timestamp given rather than clamping it forward, which is what makes it
    /// usable for importing records whose creation time is already known. Because it does
    /// not participate in the monotonic sequence, ids from this overload are ordered only
    /// relative to their own timestamps. Uniqueness within a millisecond comes from random
    /// bits instead of the counter.
    /// </remarks>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> random = stackalloc byte[2];
        Random.Shared.NextBytes(random);
        var randA = (ushort)(((random[0] << 8) | random[1]) & 0x0FFF);
        return Build(timestamp.ToUnixTimeMilliseconds(), randA);
    }

    /// <summary>
    /// Issues the next id in the monotonic sequence, as of <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Internal so that the ordering guarantees can be tested against a controlled clock:
    /// the behaviour under a backwards clock step is not otherwise reachable without
    /// changing the system time.
    /// </remarks>
    internal static Guid NewMonotonic(DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();

        // Ids issued within one millisecond must still order correctly, so a counter occupies
        // rand_a rather than random bits. This is the "replace leftmost random bits with
        // increased clock precision" method described by RFC 9562.
        ushort counter;
        lock (Gate)
        {
            if (ms > _lastMs)
            {
                _lastMs = ms;
                _counter = 0;
            }
            else
            {
                // Also covers a clock that steps backwards, from time synchronisation or a
                // daylight saving change: the previous timestamp is held rather than issuing
                // an id that sorts before one already handed out.
                ms = _lastMs;
                _counter++;
            }

            counter = _counter;
        }

        return Build(ms, counter);
    }

    private static Guid Build(long unixMs, ushort randA)
    {
        Span<byte> bytes = stackalloc byte[16];

        // unix_ts_ms: 48 bits, big-endian.
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // ver (4 bits) = 7, then 12 bits of rand_a.
        bytes[6] = (byte)(0x70 | ((randA >> 8) & 0x0F));
        bytes[7] = (byte)randA;

        Random.Shared.NextBytes(bytes[8..]);

        // var (2 bits) = 0b10.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return FromBigEndian(bytes);
    }

    /// <summary>
    /// Reads a Guid from its RFC 9562 big-endian byte order.
    /// </summary>
    /// <remarks>
    /// Guid's own layout stores the first three fields little-endian on x86, which would
    /// scramble the timestamp prefix and destroy the sort ordering that is the entire
    /// point of version 7. All persistence must round-trip through this pair.
    /// </remarks>
    public static Guid FromBigEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            throw new ArgumentException("A UUID is exactly 16 bytes.", nameof(bytes));
        }

        var a = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        var b = (short)((bytes[4] << 8) | bytes[5]);
        var c = (short)((bytes[6] << 8) | bytes[7]);
        return new Guid(a, b, c, bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
    }

    /// <summary>Writes a Guid in RFC 9562 big-endian byte order.</summary>
    public static byte[] ToBigEndian(Guid value)
    {
        var bytes = value.ToByteArray();
        // Reverse the three little-endian fields back to network order.
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
        return bytes;
    }

    /// <summary>Extracts the embedded creation timestamp from a version 7 UUID.</summary>
    public static DateTimeOffset GetTimestamp(Guid value)
    {
        var b = ToBigEndian(value);
        long ms = ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24)
                  | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }
}
