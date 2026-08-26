using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Runs a real scan over real files into a real database.
/// </summary>
/// <remarks>
/// The use case tests cover the rules with every port faked, which is where the logic belongs.
/// This covers what those cannot: that the pieces are wired together correctly, that
/// directory walking and the upsert agree about what a path is, and that a second scan of an
/// untouched folder really does no work. Those are integration failures, and they are
/// invisible to a test whose filesystem is a dictionary.
/// </remarks>
public sealed class ScanEndToEndTests : IDisposable
{
    private readonly TemporaryDatabase _database = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"photogrouper-scan-{Guid.NewGuid():N}");

    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteScanRootRepository _roots;

    public ScanEndToEndTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _roots = new SqliteScanRootRepository(_database.Connections);
        Directory.CreateDirectory(_root);
    }

    private ScanLibraryUseCase CreateSubject() =>
        new(_roots, _photos, _photos, new LocalFileSystem(), new SystemClock());

    /// <remarks>
    /// Content is irrelevant to this milestone: the scanner reads names, sizes and timestamps,
    /// never pixels. Decoding arrives with the imaging adapter.
    /// </remarks>
    private string WriteFile(string relativePath, int bytes = 128)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    [Fact]
    public async Task Indexes_images_and_ignores_everything_else()
    {
        WriteFile("a.jpg");
        WriteFile("b.PNG");
        WriteFile(Path.Combine("trip", "c.heic"));
        WriteFile("notes.txt");
        WriteFile("movie.mp4");
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root), default);

        var result = await CreateSubject().ExecuteAsync(new NullProgressSink(), default);

        result.Added.Should().Be(3);
        (await _photos.CountAsync(default)).Should().Be(3);
    }

    [Fact]
    public async Task A_second_scan_of_an_untouched_folder_does_no_work()
    {
        WriteFile("a.jpg");
        WriteFile("b.jpg");
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root), default);
        var subject = CreateSubject();
        await subject.ExecuteAsync(new NullProgressSink(), default);

        var second = await subject.ExecuteAsync(new NullProgressSink(), default);

        second.Should().Be(new ScanResult(0, 0, 2, 0),
            "re-indexing unchanged files would turn every rescan into the full pipeline");
    }

    [Fact]
    public async Task An_edited_file_is_sent_back_through_the_pipeline()
    {
        var path = WriteFile("a.jpg", bytes: 128);
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root), default);
        var subject = CreateSubject();
        await subject.ExecuteAsync(new NullProgressSink(), default);
        var originalId = (await _photos.GetByPathAsync(path, default))!.Id;

        File.WriteAllBytes(path, new byte[512]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var second = await subject.ExecuteAsync(new NullProgressSink(), default);

        second.Updated.Should().Be(1);
        var photo = await _photos.GetByPathAsync(path, default);
        photo!.State.Should().Be(PhotoState.New);
        photo.Id.Should().Be(originalId, "face data and person names hang off this id");
    }

    [Fact]
    public async Task Walks_subfolders_when_the_root_is_recursive()
    {
        WriteFile(Path.Combine("2024", "summer", "a.jpg"));
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root, recursive: true), default);

        var result = await CreateSubject().ExecuteAsync(new NullProgressSink(), default);

        result.Added.Should().Be(1);
    }

    [Fact]
    public async Task Stays_in_the_top_folder_when_the_root_is_not_recursive()
    {
        WriteFile("top.jpg");
        WriteFile(Path.Combine("nested", "deep.jpg"));
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root, recursive: false), default);

        var result = await CreateSubject().ExecuteAsync(new NullProgressSink(), default);

        result.Added.Should().Be(1);
    }

    [Fact]
    public async Task Records_the_real_size_and_modified_time_from_disk()
    {
        var path = WriteFile("a.jpg", bytes: 4096);
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root), default);

        await CreateSubject().ExecuteAsync(new NullProgressSink(), default);

        var photo = await _photos.GetByPathAsync(path, default);
        photo!.FileSize.Should().Be(4096);
        photo.ModifiedUtc.Should().BeCloseTo(
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Handles_a_folder_with_no_images_at_all()
    {
        WriteFile("readme.txt");
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), _root), default);

        var result = await CreateSubject().ExecuteAsync(new NullProgressSink(), default);

        result.Should().Be(new ScanResult(0, 0, 0, 0));
    }

    public void Dispose()
    {
        _database.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory is the OS's problem if a handle is still open.
        }
    }

    private sealed class NullProgressSink : IProgressSink
    {
        public void Report(ProgressUpdate update)
        {
        }
    }
}
