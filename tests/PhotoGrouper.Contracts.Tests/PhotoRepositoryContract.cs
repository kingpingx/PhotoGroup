using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Contracts.Tests;

/// <summary>
/// The behaviour every photo store must exhibit, whichever backend implements it.
/// </summary>
/// <remarks>
/// This is where substitutability stops being an aspiration and becomes a definition. The
/// storage backend was left replaceable on purpose, and the only way to know whether a second
/// implementation really is a drop-in replacement is for it to inherit this class and pass it
/// unchanged. Anything asserted here is part of the contract; anything not asserted here an
/// adapter is free to decide for itself.
///
/// Deliberately says nothing about SQL, connections or transactions, so that a document store
/// could satisfy it without contortion.
/// </remarks>
public abstract class PhotoRepositoryContract
{
    private static readonly DateTimeOffset Modified = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a store with no photos in it.</summary>
    protected abstract Task<(IPhotoReader Reader, IPhotoWriter Writer)> CreateAsync();

    private static Photo NewPhoto(string path, long size = 1000, PhotoState state = PhotoState.New) =>
        new(PhotoId.New(), path, size, Modified, state: state);

    [Fact]
    public async Task An_empty_store_holds_nothing()
    {
        var (reader, _) = await CreateAsync();

        (await reader.CountAsync(default)).Should().Be(0);
        (await reader.GetByPathAsync(@"D:\nope.jpg", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_photo_can_be_read_back_by_path()
    {
        var (reader, writer) = await CreateAsync();
        await writer.UpsertAsync(NewPhoto(@"D:\photos\a.jpg"), default);

        var found = await reader.GetByPathAsync(@"D:\photos\a.jpg", default);

        found.Should().NotBeNull();
        found!.Path.Should().Be(@"D:\photos\a.jpg");
    }

    [Fact]
    public async Task A_stored_photo_can_be_read_back_by_id()
    {
        var (reader, writer) = await CreateAsync();
        var photo = NewPhoto(@"D:\photos\a.jpg");
        await writer.UpsertAsync(photo, default);

        (await reader.GetByIdAsync(photo.Id, default))!.Id.Should().Be(photo.Id);
    }

    [Fact]
    public async Task Every_field_survives_a_round_trip()
    {
        // Written out in full because a silently dropped column is the kind of defect that
        // shows up much later as a photo that will not display or a face box in the wrong place.
        var (reader, writer) = await CreateAsync();
        var taken = new DateTimeOffset(2024, 7, 4, 18, 30, 15, TimeSpan.Zero);
        var original = new Photo(
            PhotoId.New(), @"D:\photos\full.jpg", 123456, Modified,
            new ContentHash("ABCDEF0123456789"), 4032, 3024, orientation: 6,
            takenUtc: taken, camera: "Pixel 8", state: PhotoState.Detected,
            indexedUtc: Modified, error: null);

        await writer.UpsertAsync(original, default);
        var read = await reader.GetByPathAsync(original.Path, default);

        read.Should().BeEquivalentTo(original, options => options.ComparingByMembers<Photo>());
    }

    [Fact]
    public async Task Upserting_the_same_path_updates_rather_than_duplicates()
    {
        // The scanner identifies a file by where it lives, so re-indexing a known path has to
        // land on the existing row. Inserting a second one would give the same file two
        // identities and split its faces between them.
        var (reader, writer) = await CreateAsync();
        await writer.UpsertAsync(NewPhoto(@"D:\photos\a.jpg", size: 1000), default);

        await writer.UpsertAsync(NewPhoto(@"D:\photos\a.jpg", size: 2000), default);

        (await reader.CountAsync(default)).Should().Be(1);
        (await reader.GetByPathAsync(@"D:\photos\a.jpg", default))!.FileSize.Should().Be(2000);
    }

    [Fact]
    public async Task Photos_can_be_found_by_pipeline_state()
    {
        // How every later stage picks up its work, and how an interrupted scan resumes.
        var (reader, writer) = await CreateAsync();
        await writer.BulkUpsertAsync(
        [
            NewPhoto(@"D:\a.jpg", state: PhotoState.New),
            NewPhoto(@"D:\b.jpg", state: PhotoState.Detected),
            NewPhoto(@"D:\c.jpg", state: PhotoState.New),
        ], default);

        var pending = await reader.GetByStateAsync(PhotoState.New, 100, default);

        pending.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_state_query_respects_its_limit()
    {
        var (reader, writer) = await CreateAsync();
        await writer.BulkUpsertAsync(
            Enumerable.Range(0, 20).Select(i => NewPhoto($@"D:\{i:D3}.jpg")).ToList(), default);

        (await reader.GetByStateAsync(PhotoState.New, 5, default)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Changing_state_records_the_new_state_and_any_error()
    {
        var (reader, writer) = await CreateAsync();
        var photo = NewPhoto(@"D:\photos\broken.jpg");
        await writer.UpsertAsync(photo, default);

        await writer.SetStateAsync(photo.Id, PhotoState.Failed, "Not a valid JPEG.", default);

        var read = await reader.GetByIdAsync(photo.Id, default);
        read!.State.Should().Be(PhotoState.Failed);
        read.Error.Should().Be("Not a valid JPEG.");
    }

    [Fact]
    public async Task Updating_a_path_moves_the_photo_without_changing_its_identity()
    {
        // What a move export relies on. If the id changed here, every face and person
        // assignment attached to the photo would be orphaned by exporting it.
        var (reader, writer) = await CreateAsync();
        var photo = NewPhoto(@"D:\photos\a.jpg");
        await writer.UpsertAsync(photo, default);

        await writer.UpdatePathAsync(photo.Id, @"E:\sorted\Alice\a.jpg", default);

        (await reader.GetByPathAsync(@"D:\photos\a.jpg", default)).Should().BeNull();
        var moved = await reader.GetByPathAsync(@"E:\sorted\Alice\a.jpg", default);
        moved!.Id.Should().Be(photo.Id);
    }

    [Fact]
    public async Task Bulk_writes_store_every_item()
    {
        var (reader, writer) = await CreateAsync();

        await writer.BulkUpsertAsync(
            Enumerable.Range(0, 1000).Select(i => NewPhoto($@"D:\photos\{i:D4}.jpg")).ToList(), default);

        (await reader.CountAsync(default)).Should().Be(1000);
    }

    [Fact]
    public async Task Bulk_writing_an_empty_batch_is_harmless()
    {
        var (reader, writer) = await CreateAsync();

        await writer.BulkUpsertAsync([], default);

        (await reader.CountAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Streaming_returns_everything_in_a_stable_order()
    {
        var (reader, writer) = await CreateAsync();
        await writer.BulkUpsertAsync(
            Enumerable.Range(0, 50).Select(i => NewPhoto($@"D:\photos\{i:D3}.jpg")).ToList(), default);

        var first = await Collect(reader);
        var second = await Collect(reader);

        first.Should().HaveCount(50);
        second.Should().Equal(first, "an unstable order would make the grid reshuffle between refreshes");

        static async Task<List<string>> Collect(IPhotoReader reader)
        {
            var paths = new List<string>();
            await foreach (var photo in reader.StreamAllAsync(default))
            {
                paths.Add(photo.Path);
            }

            return paths;
        }
    }

    [Fact]
    public async Task Paths_are_matched_case_insensitively_as_Windows_does()
    {
        var (reader, writer) = await CreateAsync();
        await writer.UpsertAsync(NewPhoto(@"D:\Photos\A.jpg"), default);

        (await reader.GetByPathAsync(@"d:\photos\a.jpg", default)).Should().NotBeNull(
            "Windows treats these as the same file, so indexing both would double-count it");
    }
}
