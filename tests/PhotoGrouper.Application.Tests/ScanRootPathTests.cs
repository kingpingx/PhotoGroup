using FluentAssertions;
using PhotoGrouper.Application.UseCases;

namespace PhotoGrouper.Application.Tests;

/// <summary>
/// Covers turning a chosen folder into one canonical path.
/// </summary>
/// <remarks>
/// The duplicate check compares strings, so without this the same folder can be added more than
/// once: a trailing separator, a relative segment or a different case all describe the same
/// directory while comparing unequal. Each spelling then gets its own root and every file inside is
/// enumerated once per spelling on every scan.
/// </remarks>
public sealed class ScanRootPathTests
{
    [Fact]
    public void A_trailing_separator_is_removed() =>
        ManageScanRootsUseCase.Normalise(@"D:\photos\")
            .Should().Be(ManageScanRootsUseCase.Normalise(@"D:\photos"));

    [Fact]
    public void A_forward_slash_ending_is_removed_too() =>
        ManageScanRootsUseCase.Normalise("D:/photos/")
            .Should().Be(ManageScanRootsUseCase.Normalise(@"D:\photos"));

    [Fact]
    public void Relative_segments_are_resolved() =>
        ManageScanRootsUseCase.Normalise(@"D:\photos\2024\..")
            .Should().Be(ManageScanRootsUseCase.Normalise(@"D:\photos"));

    [Fact]
    public void A_drive_root_keeps_its_separator()
    {
        // "D:" and "D:\" are not the same thing to the filesystem: the first means the drive's
        // current directory, which is rarely its root.
        ManageScanRootsUseCase.Normalise(@"D:\").Should().EndWith(@"\");
    }

    [Fact]
    public void An_ordinary_folder_does_not_gain_a_separator() =>
        ManageScanRootsUseCase.Normalise(@"D:\photos").Should().NotEndWith(@"\");

    [Fact]
    public void Normalising_twice_changes_nothing()
    {
        var once = ManageScanRootsUseCase.Normalise(@"D:\photos\");

        ManageScanRootsUseCase.Normalise(once).Should().Be(once);
    }
}
