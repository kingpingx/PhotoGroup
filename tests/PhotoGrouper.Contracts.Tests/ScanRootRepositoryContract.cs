using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Contracts.Tests;

/// <summary>The behaviour every scan root store must exhibit.</summary>
public abstract class ScanRootRepositoryContract
{
    protected abstract Task<IScanRootRepository> CreateAsync();

    [Fact]
    public async Task An_empty_store_lists_nothing() =>
        (await (await CreateAsync()).GetAllAsync(default)).Should().BeEmpty();

    [Fact]
    public async Task An_added_root_is_listed_and_findable_by_path()
    {
        var subject = await CreateAsync();
        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\photos"), default);

        (await subject.GetAllAsync(default)).Should().ContainSingle();
        (await subject.GetByPathAsync(@"D:\photos", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Adding_the_same_path_twice_does_not_create_a_duplicate()
    {
        var subject = await CreateAsync();
        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\photos"), default);

        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\photos"), default);

        (await subject.GetAllAsync(default)).Should().ContainSingle(
            "a folder listed twice would be walked twice on every scan");
    }

    [Fact]
    public async Task Flags_survive_a_round_trip()
    {
        // The implicit flag distinguishes a folder the user chose from one the app added
        // after a move export, which is what the UI uses to decide whether it can be removed.
        var subject = await CreateAsync();
        await subject.AddAsync(
            new ScanRoot(ScanRootId.New(), @"E:\sorted", recursive: false, isImplicit: true), default);

        var root = await subject.GetByPathAsync(@"E:\sorted", default);

        root!.Recursive.Should().BeFalse();
        root.IsImplicit.Should().BeTrue();
    }

    [Fact]
    public async Task A_removed_root_is_gone()
    {
        var subject = await CreateAsync();
        var root = new ScanRoot(ScanRootId.New(), @"D:\photos");
        await subject.AddAsync(root, default);

        await subject.RemoveAsync(root.Id, default);

        (await subject.GetAllAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_last_scan_time_is_recorded()
    {
        var subject = await CreateAsync();
        var root = new ScanRoot(ScanRootId.New(), @"D:\photos");
        await subject.AddAsync(root, default);
        var when = new DateTimeOffset(2026, 8, 26, 9, 15, 0, TimeSpan.Zero);

        await subject.MarkScannedAsync(root.Id, when, default);

        (await subject.GetByPathAsync(@"D:\photos", default))!.LastScanUtc.Should().Be(when);
    }

    [Fact]
    public async Task Paths_are_matched_case_insensitively_as_Windows_does()
    {
        // The duplicate check runs through this lookup, so a case-sensitive match would let
        // the same folder be added twice and walked twice on every scan.
        var subject = await CreateAsync();
        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\Photos"), default);

        (await subject.GetByPathAsync(@"d:\photos", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Roots_are_listed_in_a_stable_order()
    {
        var subject = await CreateAsync();
        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\zebra"), default);
        await subject.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\alpha"), default);

        var paths = (await subject.GetAllAsync(default)).Select(r => r.Path);

        paths.Should().Equal(@"D:\alpha", @"D:\zebra");
    }
}
