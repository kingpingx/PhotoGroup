using FluentAssertions;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Domain.Tests;

/// <summary>
/// Covers the properties the rest of the system relies on when it uses UUIDv7 as a key.
/// </summary>
/// <remarks>
/// Version 7 was chosen over version 4 specifically so that ids sort by creation time, which
/// keeps inserts clustered at the end of an index instead of scattered across it. If
/// monotonicity or byte ordering regresses, nothing fails visibly: the app keeps working and
/// simply gets slower as the library grows. That makes these tests the only thing standing
/// between the decision and its silent reversal.
/// </remarks>
public sealed class Uuid7Tests
{
    [Fact]
    public void Sets_the_version_and_variant_fields()
    {
        var bytes = Uuid7.ToBigEndian(Uuid7.NewGuid());

        (bytes[6] >> 4).Should().Be(7, "the version nibble identifies this as a v7 UUID");
        (bytes[8] >> 6).Should().Be(0b10, "RFC 9562 requires the two-bit variant marker");
    }

    [Fact]
    public void Embeds_the_supplied_timestamp()
    {
        var when = new DateTimeOffset(2026, 8, 26, 10, 30, 0, TimeSpan.Zero);

        var recovered = Uuid7.GetTimestamp(Uuid7.NewGuid(when));

        recovered.ToUnixTimeMilliseconds().Should().Be(when.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Ids_generated_in_sequence_sort_in_generation_order()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => Uuid7.NewGuid()).ToArray();

        var asBytes = ids.Select(Uuid7.ToBigEndian).ToArray();

        for (var i = 1; i < asBytes.Length; i++)
        {
            Compare(asBytes[i - 1], asBytes[i]).Should().BeNegative(
                "ids created in a tight loop share a millisecond, so the counter in rand_a must break the tie");
        }
    }

    [Fact]
    public void A_backwards_clock_step_does_not_produce_an_out_of_order_id()
    {
        // Time synchronisation and daylight saving both step the clock backwards in practice.
        // Asked for an id "before" one already issued, the monotonic sequence holds its
        // previous timestamp rather than handing out a key that sorts into the past.
        var now = DateTimeOffset.UtcNow.AddYears(1);

        var first = Uuid7.NewMonotonic(now);
        var afterStepBack = Uuid7.NewMonotonic(now.AddMinutes(-5));

        Compare(Uuid7.ToBigEndian(first), Uuid7.ToBigEndian(afterStepBack)).Should().BeNegative();
    }

    [Fact]
    public void An_explicit_timestamp_is_honoured_rather_than_clamped_forward()
    {
        // The explicit overload deliberately does not join the monotonic sequence, so that
        // importing a record with a known creation time produces an id carrying that time.
        var past = new DateTimeOffset(2020, 3, 1, 8, 0, 0, TimeSpan.Zero);

        Uuid7.NewGuid();

        Uuid7.GetTimestamp(Uuid7.NewGuid(past)).ToUnixTimeMilliseconds()
            .Should().Be(past.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Round_trips_through_big_endian_bytes()
    {
        var original = Uuid7.NewGuid();

        Uuid7.FromBigEndian(Uuid7.ToBigEndian(original)).Should().Be(original);
    }

    [Fact]
    public void Big_endian_ordering_is_not_the_platform_ordering()
    {
        // The guard against the specific mistake this pair of helpers exists to prevent:
        // Guid.ToByteArray stores the first three fields little-endian, so writing a Guid to
        // storage with it would scramble the timestamp prefix and destroy sort order.
        var id = Uuid7.NewGuid();

        Uuid7.ToBigEndian(id).Should().NotEqual(id.ToByteArray());
    }

    [Fact]
    public void Rejects_byte_arrays_that_are_not_sixteen_bytes()
    {
        var act = () => Uuid7.FromBigEndian(new byte[15]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Generates_distinct_ids_under_concurrency()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        Parallel.For(0, 10_000, _ => ids.Add(Uuid7.NewGuid()));

        ids.Distinct().Should().HaveCount(10_000, "the counter is shared state and must be guarded");
    }

    private static int Compare(byte[] a, byte[] b) =>
        ((ReadOnlySpan<byte>)a).SequenceCompareTo(b);
}
