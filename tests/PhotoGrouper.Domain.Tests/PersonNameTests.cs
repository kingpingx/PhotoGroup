using FluentAssertions;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Domain.Tests;

/// <summary>
/// Covers what a person's name means in the domain.
/// </summary>
/// <remarks>
/// Deliberately says nothing about reserved device names or characters illegal on NTFS.
/// Those rules belong to the filesystem adapter: they describe a storage medium, not a
/// person, and a name like "CON" is perfectly valid until the moment it becomes a folder.
/// </remarks>
public sealed class PersonNameTests
{
    [Theory]
    [InlineData("Alice")]
    [InlineData("Mary-Jane O'Neill")]
    [InlineData("李雷")]
    public void Accepts_ordinary_names(string value) =>
        PersonName.Create(value).Value.Should().Be(value);

    [Fact]
    public void Trims_surrounding_whitespace() =>
        PersonName.Create("  Alice  ").Value.Should().Be("Alice");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_a_name_that_is_absent_or_only_whitespace(string? value)
    {
        PersonName.TryCreate(value, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_a_name_longer_than_the_limit()
    {
        var tooLong = new string('a', PersonName.MaxLength + 1);

        PersonName.TryCreate(tooLong, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Accepts_a_name_exactly_at_the_limit()
    {
        var atLimit = new string('a', PersonName.MaxLength);

        PersonName.TryCreate(atLimit, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Names_are_compared_by_value() =>
        PersonName.Create("Alice").Should().Be(PersonName.Create("Alice"));

    [Fact]
    public void Reserved_device_names_are_not_the_domain_s_concern() =>
        // Documents the boundary: this must succeed here and be handled by the folder
        // naming strategy when an export turns it into a directory.
        PersonName.TryCreate("CON", out _, out _).Should().BeTrue();
}
