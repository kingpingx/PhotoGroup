using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers writing the library out as one folder per person, and putting a move back.
/// </summary>
/// <remarks>
/// This is the second operation in the application that touches somebody's own files, and the only
/// one that can relocate an original. Run against a real filesystem rather than a fake, because
/// what is being checked is not that a copy happens — that is one call — but everything around it:
/// that a photograph of two people reaches both folders, that a name which cannot be a folder still
/// becomes one, that a move rewrites the library's record of where the file now is, and that an
/// interrupted or completed move can be reversed from the journal alone.
///
/// The journal is the reason a move is offered at all. Without it, a move is an irreversible
/// rearrangement of somebody's photographs performed on the strength of automatic grouping.
/// </remarks>
public sealed class ExportPhotosTests : IDisposable
{
    private const string Detector = "test.detector";

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteScanRootRepository _roots;
    private readonly SqliteExportRepository _exports;
    private readonly ExportPhotosUseCase _subject;
    private readonly UndoExportUseCase _undo;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "photogrouper-export-" + Guid.NewGuid().ToString("N"));

    private string Library => Path.Combine(_root, "library");

    private string Output => Path.Combine(_root, "exported");

    public ExportPhotosTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _roots = new SqliteScanRootRepository(_database.Connections);
        _exports = new SqliteExportRepository(_database.Connections);

        var files = new LocalFileSystem();
        var clock = new SystemClock();

        _subject = new ExportPhotosUseCase(
            _people, _faces, _photos, _photos, _roots, _exports, files, clock);

        _undo = new UndoExportUseCase(_exports, _photos, files, clock);

        Directory.CreateDirectory(Library);
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private async Task<PersonId> AddPersonAsync(string name)
    {
        var person = new Person(PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch);
        await _people.AddAsync(person, default);
        return person.Id;
    }

    /// <summary>Writes a real file and indexes it, with a face per person named.</summary>
    private async Task<PhotoId> AddPhotoAsync(
        string fileName, string contents, params PersonId[] appearing)
    {
        var path = Path.Combine(Library, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);

        var photo = new Photo(
            PhotoId.New(), path, new FileInfo(path).Length, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var faces = appearing
            .Select(_ => new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks))
            .ToList();

        if (faces.Count > 0)
        {
            await _faces.BulkInsertAsync(faces, default);
            await _faces.AssignAsync(
                [.. faces.Zip(appearing, (face, person) =>
                    new FaceAssignment(face.Id, person, Assignment.Auto))],
                default);
        }

        return photo.Id;
    }

    private Task<ExportResult> ExportAsync(ExportMode mode, params PersonId[] who) =>
        _subject.ExecuteAsync(
            new ExportRequest(
                Output, mode,
                who.Length == 0 ? ExportSource.EveryNamedPerson : ExportSource.ChosenPeople,
                who, Detector),
            NullProgress.Instance,
            default);

    [Fact]
    public async Task Each_person_gets_a_folder_of_their_photographs()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("1.jpg", "one", alice);
        await AddPhotoAsync("2.jpg", "two", bob);

        var result = await ExportAsync(ExportMode.Copy);

        result.IsSuccess.Should().BeTrue();
        result.Written.Should().Be(2);

        File.Exists(Path.Combine(Output, "Alice", "1.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(Output, "Bob", "2.jpg")).Should().BeTrue();
    }

    /// <remarks>
    /// There is no answer to "which of them does this photograph belong to" that is right, so it
    /// goes to both. Filing it under only the first would make the folders quietly wrong for
    /// everybody else in the picture.
    /// </remarks>
    [Fact]
    public async Task A_photograph_of_two_people_reaches_both_folders()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("together.jpg", "both", alice, bob);

        await ExportAsync(ExportMode.Copy);

        File.Exists(Path.Combine(Output, "Alice", "together.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(Output, "Bob", "together.jpg")).Should().BeTrue();
    }

    /// <remarks>
    /// A copy can be in two folders; a file cannot. A move keeps one destination rather than
    /// pretending to put the same file in both and failing on the second.
    /// </remarks>
    [Fact]
    public async Task A_moved_photograph_of_two_people_goes_to_one_folder_only()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("together.jpg", "both", alice, bob);

        var result = await ExportAsync(ExportMode.Move);

        result.Written.Should().Be(1);
        result.FailedCount.Should().Be(0, "the second folder must not be attempted and fail");
    }

    [Fact]
    public async Task Copying_leaves_the_original_where_it_was()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        await ExportAsync(ExportMode.Copy);

        File.Exists(Path.Combine(Library, "1.jpg")).Should().BeTrue();
    }

    /// <remarks>
    /// Without this the photograph is still indexed at a path holding nothing, so every thumbnail
    /// and every face on it breaks at once, and the next scan re-indexes it at its new home as a
    /// stranger with no name.
    /// </remarks>
    [Fact]
    public async Task Moving_rewrites_the_librarys_record_of_where_the_file_is()
    {
        var alice = await AddPersonAsync("Alice");
        var photo = await AddPhotoAsync("1.jpg", "one", alice);

        await ExportAsync(ExportMode.Move);

        File.Exists(Path.Combine(Library, "1.jpg")).Should().BeFalse();

        (await _photos.GetByIdAsync(photo, default))!.Path
            .Should().Be(Path.Combine(Output, "Alice", "1.jpg"));
    }

    /// <remarks>
    /// The rules for what may be a folder belong to the filesystem, not to a person, and a name made
    /// of forbidden characters is still somebody.
    /// </remarks>
    [Fact]
    public async Task A_name_that_cannot_be_a_folder_still_becomes_one()
    {
        var awkward = await AddPersonAsync("Bob: the 2nd / Jr.");
        await AddPhotoAsync("1.jpg", "one", awkward);

        var result = await ExportAsync(ExportMode.Copy);

        result.Written.Should().Be(1);
        Directory.GetDirectories(Output).Should().ContainSingle();
    }

    [Fact]
    public async Task Two_people_sharing_a_file_name_do_not_collide()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync(Path.Combine("a", "IMG_1.jpg"), "alice", alice);
        await AddPhotoAsync(Path.Combine("b", "IMG_1.jpg"), "bob", bob);

        await ExportAsync(ExportMode.Copy);

        (await File.ReadAllTextAsync(Path.Combine(Output, "Alice", "IMG_1.jpg"))).Should().Be("alice");
        (await File.ReadAllTextAsync(Path.Combine(Output, "Bob", "IMG_1.jpg"))).Should().Be("bob");
    }

    /// <remarks>
    /// Running the same export twice is an ordinary thing to do after adding photographs, and it
    /// should cost only the new ones rather than rewriting everything.
    /// </remarks>
    [Fact]
    public async Task Running_the_same_export_again_skips_what_is_already_there()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        await ExportAsync(ExportMode.Copy);
        var second = await ExportAsync(ExportMode.Copy);

        second.Written.Should().Be(0);
        second.Skipped.Should().Be(1);
    }

    /// <remarks>
    /// Exporting into a scanned folder makes the next scan index the copies as new photographs,
    /// detect faces in them, and produce a second copy of everybody.
    /// </remarks>
    [Fact]
    public async Task Writing_into_a_scanned_folder_is_refused()
    {
        await _roots.AddAsync(new ScanRoot(ScanRootId.New(), Library), default);
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        var inside = Path.Combine(Library, "exported");
        var result = await _subject.ExecuteAsync(
            new ExportRequest(inside, ExportMode.Copy, ExportSource.EveryNamedPerson, [], Detector),
            NullProgress.Instance, default);

        result.IsSuccess.Should().BeFalse();
        Directory.Exists(inside).Should().BeFalse("nothing should be created for a refused run");
    }

    [Fact]
    public async Task Only_the_people_chosen_are_written()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("1.jpg", "one", alice);
        await AddPhotoAsync("2.jpg", "two", bob);

        await ExportAsync(ExportMode.Copy, alice);

        Directory.Exists(Path.Combine(Output, "Alice")).Should().BeTrue();
        Directory.Exists(Path.Combine(Output, "Bob")).Should().BeFalse();
    }

    /// <remarks>
    /// The reason the journal is written to disk before any file is touched. A move is the one
    /// operation here that leaves somebody's photographs somewhere they did not put them.
    /// </remarks>
    [Fact]
    public async Task A_move_can_be_put_back()
    {
        var alice = await AddPersonAsync("Alice");
        var photo = await AddPhotoAsync("1.jpg", "one", alice);

        var exported = await ExportAsync(ExportMode.Move);
        var result = await _undo.ExecuteAsync(exported.RunId, NullProgress.Instance, default);

        result.IsSuccess.Should().BeTrue();
        result.Restored.Should().Be(1);

        File.Exists(Path.Combine(Library, "1.jpg")).Should().BeTrue();
        (await _photos.GetByIdAsync(photo, default))!.Path
            .Should().Be(Path.Combine(Library, "1.jpg"), "the index follows the file back");
    }

    [Fact]
    public async Task A_copy_is_never_offered_an_undo()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        var exported = await ExportAsync(ExportMode.Copy);
        var result = await _undo.ExecuteAsync(exported.RunId, NullProgress.Instance, default);

        result.IsSuccess.Should().BeFalse(
            "nothing was taken away, so deleting the copies would be destruction dressed as a fix");
        File.Exists(Path.Combine(Output, "Alice", "1.jpg")).Should().BeTrue();
    }

    /// <remarks>
    /// Whatever is back at the original path was not put there by this run, and is not this
    /// operation's to destroy.
    /// </remarks>
    [Fact]
    public async Task Undo_refuses_to_overwrite_something_at_the_original_path()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        var exported = await ExportAsync(ExportMode.Move);
        await File.WriteAllTextAsync(Path.Combine(Library, "1.jpg"), "something else");

        var result = await _undo.ExecuteAsync(exported.RunId, NullProgress.Instance, default);

        result.Restored.Should().Be(0);
        result.Blocked.Should().Be(1);
        (await File.ReadAllTextAsync(Path.Combine(Library, "1.jpg"))).Should().Be("something else");
    }

    [Fact]
    public async Task Undoing_twice_is_refused_rather_than_repeated()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);

        var exported = await ExportAsync(ExportMode.Move);
        await _undo.ExecuteAsync(exported.RunId, NullProgress.Instance, default);

        var second = await _undo.ExecuteAsync(exported.RunId, NullProgress.Instance, default);

        second.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_is_recorded_with_everything_it_planned()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("1.jpg", "one", alice);
        await AddPhotoAsync("2.jpg", "two", alice);

        var exported = await ExportAsync(ExportMode.Copy);

        var ops = await _exports.GetOpsAsync(exported.RunId, default);
        ops.Should().HaveCount(2);
        ops.Should().OnlyContain(op => op.Status == ExportOpStatus.Done);

        (await _exports.GetRecentRunsAsync(10, default)).Should().ContainSingle();
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
