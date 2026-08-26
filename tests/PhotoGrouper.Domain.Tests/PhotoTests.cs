using FluentAssertions;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Domain.Tests;

public sealed class PhotoTests
{
    private static readonly DateTimeOffset Modified = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Photo Create(long size = 1000, DateTimeOffset? modified = null) =>
        new(PhotoId.New(), @"D:\photos\a.jpg", size, modified ?? Modified);

    [Fact]
    public void Is_unchanged_when_size_and_modified_time_both_match() =>
        Create().HasChanged(1000, Modified).Should().BeFalse();

    [Fact]
    public void Has_changed_when_the_file_grew_or_shrank() =>
        Create().HasChanged(2000, Modified).Should().BeTrue();

    [Fact]
    public void Has_changed_when_the_modified_time_moved() =>
        Create().HasChanged(1000, Modified.AddSeconds(1)).Should().BeTrue();

    [Fact]
    public void Relocating_rewrites_the_path()
    {
        // What a move export does after the file lands. Without it the index points at a
        // path that no longer exists and every thumbnail for the photo breaks.
        var photo = Create();

        photo.RelocateTo(@"E:\sorted\Alice\a.jpg");

        photo.Path.Should().Be(@"E:\sorted\Alice\a.jpg");
    }

    [Fact]
    public void Rejects_an_empty_path()
    {
        var act = () => new Photo(PhotoId.New(), "  ", 1, Modified);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Starts_in_the_New_state() => Create().State.Should().Be(PhotoState.New);
}
