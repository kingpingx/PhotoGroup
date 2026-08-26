using FluentAssertions;
using PhotoGrouper.Application.Tests.Fakes;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.Tests;

/// <summary>
/// Covers the incremental scan.
/// </summary>
/// <remarks>
/// The behaviour that matters most here is what the scanner does the second time it runs.
/// Re-processing unchanged files would turn a routine rescan into the full twenty minute
/// pipeline, and failing to notice a changed file would leave face data describing an image
/// that no longer exists.
/// </remarks>
public sealed class ScanLibraryUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Root = @"D:\photos";

    private readonly FakeFileSystem _fileSystem = new();
    private readonly InMemoryPhotoRepository _photos = new();
    private readonly InMemoryScanRootRepository _roots = new();
    private readonly RecordingProgressSink _progress = new();

    private ScanLibraryUseCase CreateSubject() =>
        new(_roots, _photos, _photos, _fileSystem, new FixedClock(Now));

    private async Task AddRootAsync(string path = Root)
    {
        _fileSystem.AddDirectory(path);
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), path), CancellationToken.None);
    }

    [Fact]
    public async Task Reports_nothing_when_no_folders_are_configured()
    {
        var result = await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        result.Should().Be(new ScanResult(0, 0, 0, 0));
    }

    [Fact]
    public async Task Indexes_files_it_has_not_seen_before()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg").AddFile(@"D:\photos\holiday\b.png");

        var result = await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        result.Added.Should().Be(2);
        (await _photos.CountAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Ignores_files_that_are_not_images()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg")
                   .AddFile(@"D:\photos\notes.txt")
                   .AddFile(@"D:\photos\clip.mp4");

        var result = await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        result.Added.Should().Be(1, "video and text files are outside the scope of this app");
    }

    [Fact]
    public async Task Picks_up_iPhone_HEIC_files()
    {
        // Skipping HEIC would silently miss most of a phone-sourced library, which reads as
        // "the app found nothing" rather than as an unsupported format.
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\IMG_0001.HEIC");

        var result = await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        result.Added.Should().Be(1);
    }

    [Fact]
    public async Task Leaves_unchanged_files_alone_on_a_second_scan()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg");
        var subject = CreateSubject();
        await subject.ExecuteAsync(_progress, CancellationToken.None);

        var second = await subject.ExecuteAsync(_progress, CancellationToken.None);

        second.Should().Be(new ScanResult(0, 0, 1, 0));
    }

    [Fact]
    public async Task Re_queues_a_file_whose_contents_changed()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg", length: 1000);
        var subject = CreateSubject();
        await subject.ExecuteAsync(_progress, CancellationToken.None);

        // Simulate an edit: the file is larger and its timestamp has moved.
        _fileSystem.Touch(@"D:\photos\a.jpg", 2000, Now);
        var second = await subject.ExecuteAsync(_progress, CancellationToken.None);

        second.Updated.Should().Be(1);

        var photo = await _photos.GetByPathAsync(@"D:\photos\a.jpg", CancellationToken.None);
        photo!.State.Should().Be(PhotoState.New,
            "everything derived from the old pixels is stale, so the file goes back through the whole pipeline");
    }

    [Fact]
    public async Task Keeps_the_original_id_when_a_file_is_re_indexed()
    {
        // Face rows and person assignments hang off the photo id. Issuing a new one on every
        // edit would orphan them, quietly discarding naming work the user cannot get back.
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg", length: 1000);
        var subject = CreateSubject();
        await subject.ExecuteAsync(_progress, CancellationToken.None);
        var originalId = (await _photos.GetByPathAsync(@"D:\photos\a.jpg", CancellationToken.None))!.Id;

        _fileSystem.Touch(@"D:\photos\a.jpg", 2000, Now);
        await subject.ExecuteAsync(_progress, CancellationToken.None);

        (await _photos.GetByPathAsync(@"D:\photos\a.jpg", CancellationToken.None))!.Id.Should().Be(originalId);
    }

    [Fact]
    public async Task Skips_an_unreachable_folder_without_forgetting_its_photos()
    {
        // A root on a drive that is not currently plugged in. Treating this as "the files
        // were deleted" would throw away every detection and name for that drive, and the
        // user would pay for it again when they reconnected it.
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg");
        var subject = CreateSubject();
        await subject.ExecuteAsync(_progress, CancellationToken.None);

        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), @"E:\offline"), CancellationToken.None);
        var second = await subject.ExecuteAsync(_progress, CancellationToken.None);

        second.SkippedRoots.Should().Be(1);
        (await _photos.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Writes_in_batches_rather_than_one_row_at_a_time()
    {
        await AddRootAsync();
        for (var i = 0; i < 1200; i++)
        {
            _fileSystem.AddFile($@"D:\photos\{i:D5}.jpg");
        }

        await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        _photos.BulkUpsertCalls.Should().BeLessThan(10,
            "row-at-a-time insertion is what makes a large scan take tens of minutes instead of a few");
    }

    [Fact]
    public async Task Stops_promptly_when_cancelled_and_keeps_what_it_already_wrote()
    {
        await AddRootAsync();
        for (var i = 0; i < 2000; i++)
        {
            _fileSystem.AddFile($@"D:\photos\{i:D5}.jpg");
        }

        using var cancellation = new CancellationTokenSource();

        // Fire the moment the first batch has been committed: work is durable and more
        // remains, which is exactly the state a resumable scan has to survive.
        _photos.AfterBulkUpsert = () => cancellation.Cancel();

        var act = async () => await CreateSubject().ExecuteAsync(_progress, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        (await _photos.CountAsync(CancellationToken.None)).Should().BeGreaterThan(0)
            .And.Subject.Should().NotBe(2000,
                "a cancelled scan keeps what it recorded and resumes from there, rather than starting over");
    }

    [Fact]
    public async Task Reports_progress_as_it_goes()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg");

        await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        _progress.Updates.Should().NotBeEmpty();
        _progress.Updates[^1].Total.Should().NotBeNull("the final report knows the true total");
    }

    [Fact]
    public async Task Records_when_each_root_was_last_scanned()
    {
        await AddRootAsync();
        _fileSystem.AddFile(@"D:\photos\a.jpg");

        await CreateSubject().ExecuteAsync(_progress, CancellationToken.None);

        _roots.MarkedScans.Should().ContainSingle().Which.Should().Be(Now);
    }
}
