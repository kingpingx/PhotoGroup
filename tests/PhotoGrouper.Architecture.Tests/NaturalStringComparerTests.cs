using FluentAssertions;
using PhotoGrouper.App.Services;

namespace PhotoGrouper.Architecture.Tests;

/// <summary>
/// Covers ordering names the way a person reads them.
/// </summary>
/// <remarks>
/// Plain string comparison puts "10" before "2", which produced the order 1, 10, 11, 12, 13, 14,
/// 15, 16, 2, 3 on a People screen and reads as broken. These pin the numeric handling, which is
/// where the subtle mistakes live: leading zeroes, numbers too long for an integer, and digits
/// embedded in the middle of a name.
/// </remarks>
public sealed class NaturalStringComparerTests
{
    private static List<string> Sorted(params string[] values) =>
        [.. values.OrderBy(v => v, NaturalStringComparer.Instance)];

    [Fact]
    public void Numbers_are_ordered_by_value_not_by_first_digit() =>
        Sorted("1", "10", "11", "2", "3", "16")
            .Should().Equal("1", "2", "3", "10", "11", "16");

    [Fact]
    public void Names_ending_in_numbers_group_naturally() =>
        Sorted("Person 10", "Person 2", "Person 1")
            .Should().Equal("Person 1", "Person 2", "Person 10");

    [Fact]
    public void Ordinary_names_still_sort_alphabetically() =>
        Sorted("Charlie", "alice", "Bob")
            .Should().Equal("alice", "Bob", "Charlie");

    [Fact]
    public void Case_is_ignored_as_it_is_elsewhere_in_the_interface() =>
        NaturalStringComparer.Instance.Compare("alice", "ALICE").Should().Be(0);

    [Fact]
    public void Leading_zeroes_do_not_change_a_number_s_position() =>
        Sorted("007", "8", "6").Should().Equal("6", "007", "8");

    [Fact]
    public void A_number_too_large_for_an_integer_still_orders_correctly()
    {
        // Parsing would overflow here; the comparison works on the digits themselves.
        Sorted("99999999999999999999999", "100000000000000000000000")
            .Should().Equal("99999999999999999999999", "100000000000000000000000");
    }

    [Fact]
    public void Digits_inside_a_name_are_compared_as_numbers() =>
        Sorted("img12b", "img2a", "img2b")
            .Should().Equal("img2a", "img2b", "img12b");

    [Fact]
    public void A_shorter_name_precedes_the_longer_one_it_begins() =>
        Sorted("Alexander", "Alex").Should().Equal("Alex", "Alexander");

    [Fact]
    public void Nulls_and_blanks_are_tolerated()
    {
        NaturalStringComparer.Instance.Compare(null, "a").Should().BeNegative();
        NaturalStringComparer.Instance.Compare("a", null).Should().BePositive();
        NaturalStringComparer.Instance.Compare(null, null).Should().Be(0);
        NaturalStringComparer.Instance.Compare("", "").Should().Be(0);
    }

    [Fact]
    public void The_order_is_consistent_in_both_directions()
    {
        // An inconsistent comparer makes a sort throw, and only for some inputs, which is a
        // miserable thing to diagnose later.
        string[] values = ["1", "10", "2", "Alice", "alice", "007", "7", "img2a", ""];

        foreach (var a in values)
        {
            foreach (var b in values)
            {
                var forward = Math.Sign(NaturalStringComparer.Instance.Compare(a, b));
                var backward = Math.Sign(NaturalStringComparer.Instance.Compare(b, a));
                forward.Should().Be(-backward, $"comparing '{a}' with '{b}' must be symmetric");
            }
        }
    }
}
