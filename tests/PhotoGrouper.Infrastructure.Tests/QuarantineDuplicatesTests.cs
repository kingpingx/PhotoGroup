using FluentAssertions;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers moving chosen duplicates out of the library.
/// </summary>
/// <remarks>
/// The only operation in this application that touches a user's own files, so these run against a
/// real filesystem rather than a fake. What is being checked is not that a move happens — that is
/// one call — but that nothing is lost around it: that a name collision does not overwrite the
/// file being preserved, that a file which will not move keeps its place in the library, and that
/// the index never loses a photograph whose file is still sitting where it was.
/// </remarks>
public sealed class QuarantineDuplicatesTests : IDisposable
{
    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteScanRootRepository _roots;
    private readonly QuarantineDuplicatesUseCase _subject;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "photogrouper-quarantine-" + Guid.NewGuid().ToString("N"));

    private string Library => Path.Combine(_root, "library");

    private string Quarantine => Path.Combine(_root, "duplicates");

    public QuarantineDuplicatesTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _roots = new SqliteScanRootRepository(_database.Connections);
        _subject = new QuarantineDuplicatesUseCase(_photos, _photos, _roots, new LocalFileSystem());

        Directory.CreateDirectory(Library);
    }

    /// <summary>Writes a file and indexes it, as a scan would.</summary>
    private async Task<PhotoId> AddAsync(string name, string contents = "photo", string? folder = null)
    {
        var directory = folder ?? Library;
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, contents);

        var photo = new Photo(
            PhotoId.New(), path, new FileInfo(path).Length, DateTimeOffset.UnixEpoch);

        await _photos.UpsertAsync(photo, default);
        return photo.Id;
    }

    [Fact]
    public async Task A_chosen_photograph_leaves_the_folder_and_the_library()
    {
        var keep = await AddAsync("keep.jpg");
        var extra = await AddAsync("extra.jpg");

        var result = await _subject.ExecuteAsync([extra], Quarantine, default);

        result.IsSuccess.Should().BeTrue();
        result.Moved.Should().Be(1);

        File.Exists(Path.Combine(Library, "extra.jpg")).Should().BeFalse();
        File.Exists(Path.Combine(Quarantine, "extra.jpg")).Should().BeTrue();

        (await _photos.GetByIdAsync(extra, default)).Should().BeNull();
        (await _photos.GetByIdAsync(keep, default)).Should().NotBeNull("the keeper is untouched");
        File.Exists(Path.Combine(Library, "keep.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task The_destination_folder_is_created_if_it_is_not_there()
    {
        var extra = await AddAsync("extra.jpg");

        Directory.Exists(Quarantine).Should().BeFalse();

        await _subject.ExecuteAsync([extra], Quarantine, default);

        Directory.Exists(Quarantine).Should().BeTrue();
    }

    /// <remarks>
    /// The case that would destroy data if it were got wrong. Duplicates very often share a name —
    /// that is frequently how they came to be duplicates — so two files landing on one destination
    /// is the normal case, and overwriting would delete the very file being preserved.
    /// </remarks>
    [Fact]
    public async Task Two_duplicates_with_the_same_name_both_survive()
    {
        var first = await AddAsync("IMG_1234.jpg", "first copy", Path.Combine(Library, "camera"));
        var second = await AddAsync("IMG_1234.jpg", "second copy", Path.Combine(Library, "backup"));

        var result = await _subject.ExecuteAsync([first, second], Quarantine, default);

        result.Moved.Should().Be(2);

        var landed = Directory.GetFiles(Quarantine).Select(File.ReadAllText).ToList();
        landed.Should().BeEquivalentTo(["first copy", "second copy"],
            "neither copy may overwrite the other");
    }

    /// <remarks>
    /// The destination may already hold a file of that name from an earlier run, which is not this
    /// run's to overwrite.
    /// </remarks>
    [Fact]
    public async Task A_file_already_in_the_destination_is_not_overwritten()
    {
        Directory.CreateDirectory(Quarantine);
        await File.WriteAllTextAsync(Path.Combine(Quarantine, "extra.jpg"), "from an earlier run");

        var extra = await AddAsync("extra.jpg", "from this run");

        await _subject.ExecuteAsync([extra], Quarantine, default);

        (await File.ReadAllTextAsync(Path.Combine(Quarantine, "extra.jpg")))
            .Should().Be("from an earlier run");

        Directory.GetFiles(Quarantine).Should().HaveCount(2);
    }

    /// <remarks>
    /// Somebody may move a file themselves between finding the duplicates and confirming them.
    /// Dropping it from the index is what they asked for, so this is not a failure.
    /// </remarks>
    [Fact]
    public async Task A_file_that_has_already_gone_is_dropped_from_the_library()
    {
        var extra = await AddAsync("extra.jpg");
        File.Delete(Path.Combine(Library, "extra.jpg"));

        var result = await _subject.ExecuteAsync([extra], Quarantine, default);

        result.IsSuccess.Should().BeTrue();
        result.AlreadyGone.Should().Be(1);
        result.Moved.Should().Be(0);
        (await _photos.GetByIdAsync(extra, default)).Should().BeNull();
    }

    [Fact]
    public async Task Nothing_selected_is_refused_rather_than_silently_doing_nothing()
    {
        var result = await _subject.ExecuteAsync([], Quarantine, default);

        result.IsSuccess.Should().BeFalse();
        Directory.Exists(Quarantine).Should().BeFalse("nothing should be created for an empty run");
    }

    [Fact]
    public async Task A_missing_destination_is_refused()
    {
        var extra = await AddAsync("extra.jpg");

        var result = await _subject.ExecuteAsync([extra], "   ", default);

        result.IsSuccess.Should().BeFalse();
        File.Exists(Path.Combine(Library, "extra.jpg")).Should().BeTrue("nothing moved");
        (await _photos.GetByIdAsync(extra, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_reported_size_is_what_actually_moved()
    {
        var extra = await AddAsync("extra.jpg", new string('x', 4096));

        var result = await _subject.ExecuteAsync([extra], Quarantine, default);

        result.BytesRecovered.Should().Be(4096);
    }

    /// <remarks>
    /// A file that will not move keeps its place in the library, because the index must never lose
    /// a photograph whose file is still sitting where it was. That is the one outcome nothing here
    /// could put right afterwards.
    /// </remarks>
    [Fact]
    public async Task A_file_that_cannot_move_keeps_its_place_in_the_library()
    {
        var locked = await AddAsync("locked.jpg");
        var free = await AddAsync("free.jpg");

        // Held open with no sharing, which is what an image viewer or a sync client does.
        using (var hold = new FileStream(
                   Path.Combine(Library, "locked.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _subject.ExecuteAsync([locked, free], Quarantine, default);

            result.Moved.Should().Be(1, "only the file that was free could move");
        }

        (await _photos.GetByIdAsync(locked, default))
            .Should().NotBeNull("its file is still in the library folder");
        File.Exists(Path.Combine(Library, "locked.jpg")).Should().BeTrue();

        (await _photos.GetByIdAsync(free, default)).Should().BeNull();
    }

    /// <remarks>
    /// The trap this closes: moving duplicates into a scanned folder undoes the operation at the
    /// next scan, and the user is offered the same decision over again with no clue why.
    /// </remarks>
    [Fact]
    public async Task A_destination_inside_a_scanned_folder_is_refused()
    {
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), Library), default);
        var extra = await AddAsync("extra.jpg");

        var inside = Path.Combine(Library, "duplicates");
        var result = await _subject.ExecuteAsync([extra], inside, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("scanned");
        File.Exists(Path.Combine(Library, "extra.jpg")).Should().BeTrue("nothing moved");
        (await _photos.GetByIdAsync(extra, default)).Should().NotBeNull();
    }

    /// <remarks>
    /// Compared segment-wise rather than by a plain prefix test: a sibling folder whose name merely
    /// begins with a scan root's name is outside it, and refusing it would be wrong.
    /// </remarks>
    [Fact]
    public async Task A_sibling_folder_with_a_similar_name_is_allowed()
    {
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), Library), default);
        var extra = await AddAsync("extra.jpg");

        var sibling = Library + "Backup";
        var result = await _subject.ExecuteAsync([extra], sibling, default);

        result.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(sibling, "extra.jpg")).Should().BeTrue();

        Directory.Delete(sibling, recursive: true);
    }

    public void Dispose()
    {
        _database.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
