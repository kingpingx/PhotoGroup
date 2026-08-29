using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers deciding which photographs are the same picture.
/// </summary>
/// <remarks>
/// The output of this is a list of files offered for moving off somebody's disk, so the tests are
/// written around the ways it could be wrong rather than the ways it is right: a set that pulls in
/// a photograph taken a year apart, a chain that swallows a whole library one near-match at a time,
/// a keeper chosen badly enough that the frame worth having is the one moved away.
///
/// Fingerprints are written directly rather than produced from images. What the fingerprint of a
/// given picture is belongs to the tests of the algorithm; what is done with two fingerprints a
/// given distance apart belongs here, and building images to reach a chosen distance would couple
/// these tests to an algorithm they are not about.
/// </remarks>
public sealed class FindDuplicatePhotosTests : IDisposable
{
    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqlitePhotoSignatureRepository _signatures;
    private readonly FindDuplicatePhotosUseCase _subject;

    public FindDuplicatePhotosTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _signatures = new SqlitePhotoSignatureRepository(_database.Connections);
        _subject = new FindDuplicatePhotosUseCase(_signatures, _photos);
    }

    private static readonly DateTimeOffset Noon =
        new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A fingerprint differing from the base one in the given number of bits.</summary>
    /// <remarks>
    /// Built by setting that many low bits, so a caller asking for a distance of three gets exactly
    /// three. The specific bits carry no meaning; only how many differ does.
    /// </remarks>
    private static PerceptualHash HashAtDistance(int bits) =>
        new(bits <= 0 ? 0UL : (1UL << bits) - 1, 0UL);

    private async Task<PhotoId> AddAsync(
        string name,
        PerceptualHash hash,
        double sharpness = 100,
        long size = 1000,
        DateTimeOffset? taken = null)
    {
        var photo = new Photo(
            PhotoId.New(),
            $@"D:\photos\{name}",
            size,
            Noon,
            takenUtc: taken ?? Noon);

        await _photos.UpsertAsync(photo, default);
        await _signatures.BulkUpsertAsync([new PhotoSignature(photo.Id, hash, sharpness)], default);

        return photo.Id;
    }

    private Task<IReadOnlyList<DuplicateGroup>> FindAsync(int distance = 12, TimeSpan? window = null) =>
        _subject.ExecuteAsync(distance, window ?? FindDuplicatePhotosUseCase.DefaultMaximumApart, default);

    [Fact]
    public async Task Photographs_with_the_same_fingerprint_form_one_set()
    {
        await AddAsync("a.jpg", HashAtDistance(0));
        await AddAsync("b.jpg", HashAtDistance(0));

        var groups = await FindAsync();

        groups.Should().HaveCount(1);
        groups[0].Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task Photographs_beyond_the_threshold_are_left_alone()
    {
        await AddAsync("a.jpg", HashAtDistance(0));
        await AddAsync("b.jpg", HashAtDistance(20));

        (await FindAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_photograph_matching_nothing_is_not_a_set_of_one()
    {
        await AddAsync("a.jpg", HashAtDistance(0));
        await AddAsync("b.jpg", HashAtDistance(0));
        await AddAsync("lonely.jpg", HashAtDistance(40));

        var groups = await FindAsync();

        groups.Should().HaveCount(1);
        groups[0].Members.Should().HaveCount(2);
    }

    /// <remarks>
    /// A burst is one set even though its first and last frames may be further apart than the
    /// threshold, because each frame matches the one before it. Comparing every pair and treating
    /// each match as its own group would split one burst into several overlapping sets and ask the
    /// user the same question repeatedly.
    /// </remarks>
    [Fact]
    public async Task A_chain_of_near_matches_forms_a_single_set()
    {
        await AddAsync("1.jpg", HashAtDistance(0));
        await AddAsync("2.jpg", HashAtDistance(8));
        await AddAsync("3.jpg", HashAtDistance(16));

        var groups = await FindAsync();

        groups.Should().HaveCount(1, "each frame matches its neighbour, so the burst is one set");
        groups[0].Members.Should().HaveCount(3);
    }

    /// <remarks>
    /// The guard that separates a burst from a coincidence. Two photographs of the same wall taken
    /// a year apart are not a burst, and offering to move one of them away would be worse than
    /// finding nothing at all.
    /// </remarks>
    [Fact]
    public async Task Photographs_taken_far_apart_are_not_a_burst()
    {
        await AddAsync("this-year.jpg", HashAtDistance(0), taken: Noon);
        await AddAsync("last-year.jpg", HashAtDistance(0), taken: Noon.AddYears(-1));

        (await FindAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Photographs_taken_moments_apart_are_a_burst()
    {
        await AddAsync("frame1.jpg", HashAtDistance(0), taken: Noon);
        await AddAsync("frame2.jpg", HashAtDistance(2), taken: Noon.AddSeconds(3));

        (await FindAsync()).Should().HaveCount(1);
    }

    /// <remarks>
    /// A screenshot, a scan, or anything a messaging app has stripped of its EXIF carries no
    /// capture time. Refusing to consider those would silently exclude a large part of the
    /// duplicates a real library holds, so the time guard applies only where it can.
    /// </remarks>
    [Fact]
    public async Task A_photograph_with_no_capture_time_is_still_considered()
    {
        var undated = new Photo(PhotoId.New(), @"D:\photos\screenshot.png", 1000, Noon);
        await _photos.UpsertAsync(undated, default);
        await _signatures.BulkUpsertAsync(
            [new PhotoSignature(undated.Id, HashAtDistance(0), 100)], default);

        await AddAsync("copy.png", HashAtDistance(0), taken: Noon.AddYears(-5));

        (await FindAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_sharpest_photograph_is_the_one_suggested_for_keeping()
    {
        await AddAsync("soft.jpg", HashAtDistance(0), sharpness: 10);
        await AddAsync("crisp.jpg", HashAtDistance(1), sharpness: 900);
        await AddAsync("softer.jpg", HashAtDistance(2), sharpness: 5);

        var groups = await FindAsync();

        groups[0].Best.Photo.Path.Should().EndWith("crisp.jpg");
    }

    /// <remarks>
    /// Between two frames of equal sharpness the larger file is the less re-compressed, which is
    /// the case of a photograph and the copy a messaging app made of it.
    /// </remarks>
    [Fact]
    public async Task Equally_sharp_photographs_are_settled_by_size()
    {
        await AddAsync("small.jpg", HashAtDistance(0), sharpness: 100, size: 200_000);
        await AddAsync("original.jpg", HashAtDistance(1), sharpness: 100, size: 3_000_000);

        var groups = await FindAsync();

        groups[0].Best.Photo.Path.Should().EndWith("original.jpg");
    }

    [Fact]
    public async Task The_recoverable_size_counts_everything_but_the_keeper()
    {
        await AddAsync("keep.jpg", HashAtDistance(0), sharpness: 900, size: 500);
        await AddAsync("extra1.jpg", HashAtDistance(1), sharpness: 10, size: 300);
        await AddAsync("extra2.jpg", HashAtDistance(2), sharpness: 5, size: 200);

        var groups = await FindAsync();

        groups[0].RecoverableBytes.Should().Be(500, "the keeper's own bytes are not recovered");
    }

    [Fact]
    public async Task The_biggest_set_is_offered_first()
    {
        await AddAsync("pair-a.jpg", HashAtDistance(0));
        await AddAsync("pair-b.jpg", HashAtDistance(1));

        await AddAsync("trio-a.jpg", HashAtDistance(60));
        await AddAsync("trio-b.jpg", HashAtDistance(61));
        await AddAsync("trio-c.jpg", HashAtDistance(62));

        var groups = await FindAsync();

        groups.Should().HaveCount(2);
        groups[0].Members.Should().HaveCount(3);
    }

    [Fact]
    public async Task An_empty_library_finds_nothing()
    {
        (await FindAsync()).Should().BeEmpty();
    }

    /// <remarks>
    /// A fingerprint outlives its photograph for as long as it takes a cascade to run. A set naming
    /// a photograph nothing can open has no business in front of somebody about to move files.
    /// </remarks>
    [Fact]
    public async Task A_fingerprint_whose_photograph_has_gone_is_ignored()
    {
        var kept = await AddAsync("a.jpg", HashAtDistance(0));
        var removed = await AddAsync("b.jpg", HashAtDistance(1));

        await _photos.RemoveAsync(removed, default);

        var groups = await FindAsync();

        groups.Should().BeEmpty("only one of the pair still exists");
        (await _photos.GetByIdAsync(kept, default)).Should().NotBeNull();
    }

    public void Dispose() => _database.Dispose();
}
