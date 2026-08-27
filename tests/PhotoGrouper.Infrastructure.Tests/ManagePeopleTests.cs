using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers correcting a grouping after the fact.
/// </summary>
/// <remarks>
/// Grouping by face is never right first time, so these operations are what make the automatic
/// pass safe to trust. The behaviour that matters most is the difference between removing a name
/// and rejecting a face: the first says the label was wrong and releases the faces to be grouped
/// again, the second says a particular face is not that person and must survive every future run.
/// Confusing the two would make corrections quietly undo themselves.
/// </remarks>
public sealed class ManagePeopleUseCaseTests : IDisposable
{
    private const string Detector = "test.detector";

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteClusterRepository _clusters;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly ManagePeopleUseCase _subject;

    public ManagePeopleUseCaseTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _clusters = new SqliteClusterRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);
        _subject = new ManagePeopleUseCase(_people, _faces, _clusters, _embeddings, _photos);
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

    private async Task<FaceId> AddFaceAsync(PersonId? person, string path, Assignment assignment = Assignment.Auto)
    {
        var photo = new Photo(PhotoId.New(), path, 1000, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var face = new Face(
            FaceId.New(), photo.Id, Detector, "1",
            new FaceBox(0, 0, 100, 100, 0.9f), Landmarks,
            personId: person, assignment: assignment);

        await _faces.BulkInsertAsync([face], default);
        return face.Id;
    }

    [Fact]
    public async Task A_persons_photographs_can_be_listed()
    {
        var alice = await AddPersonAsync("Alice");
        await AddFaceAsync(alice, @"D:\photos\1.jpg");
        await AddFaceAsync(alice, @"D:\photos\2.jpg");
        await AddFaceAsync(null, @"D:\photos\3.jpg");

        var found = await _subject.GetPhotosAsync(alice, Detector, default);

        found.Should().HaveCount(2);
        found.Select(p => p.Path).Should().OnlyContain(path => path.EndsWith(".jpg"));
    }

    [Fact]
    public async Task A_person_can_be_renamed()
    {
        var alice = await AddPersonAsync("Alice");

        var result = await _subject.RenameAsync(alice, "Alicia", default);

        result.IsSuccess.Should().BeTrue();
        (await _people.GetByIdAsync(alice, default))!.Name.Value.Should().Be("Alicia");
    }

    [Fact]
    public async Task Renaming_trims_surrounding_whitespace()
    {
        var alice = await AddPersonAsync("Alice");

        await _subject.RenameAsync(alice, "  Alicia  ", default);

        (await _people.GetByIdAsync(alice, default))!.Name.Value.Should().Be("Alicia");
    }

    [Fact]
    public async Task Renaming_to_an_empty_name_is_refused()
    {
        var alice = await AddPersonAsync("Alice");

        var result = await _subject.RenameAsync(alice, "   ", default);

        result.IsSuccess.Should().BeFalse();
        (await _people.GetByIdAsync(alice, default))!.Name.Value.Should().Be("Alice");
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_is_refused_and_explains_the_alternative()
    {
        // Silently merging on a rename would be surprising: the user asked to relabel one person,
        // not to fold two together. Naming a group is where merging belongs, and the message says so.
        var alice = await AddPersonAsync("Alice");
        await AddPersonAsync("Bob");

        var result = await _subject.RenameAsync(alice, "Bob", default);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("merge");
    }

    [Fact]
    public async Task Renaming_a_person_to_their_own_name_is_allowed()
    {
        var alice = await AddPersonAsync("Alice");

        (await _subject.RenameAsync(alice, "Alice", default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_person_deletes_the_name_but_keeps_the_faces()
    {
        // Removing a name says the label was wrong, not that the detections were. Deleting the
        // faces would throw away work that has to be paid for again.
        var alice = await AddPersonAsync("Alice");
        await AddFaceAsync(alice, @"D:\photos\1.jpg");
        await AddFaceAsync(alice, @"D:\photos\2.jpg");

        var result = await _subject.DeleteAsync(alice, Detector, default);

        result.IsSuccess.Should().BeTrue();
        (await _people.GetByIdAsync(alice, default)).Should().BeNull();
        (await _faces.CountAsync(Detector, activeOnly: true, default)).Should().Be(2);
    }

    [Fact]
    public async Task Removing_a_person_releases_their_faces_to_be_named_again()
    {
        var alice = await AddPersonAsync("Alice");
        await AddFaceAsync(alice, @"D:\photos\1.jpg");

        await _subject.DeleteAsync(alice, Detector, default);

        var released = new List<Face>();
        await foreach (var face in _faces.StreamByDetectorAsync(Detector, activeOnly: true, default))
        {
            released.Add(face);
        }

        released.Should().OnlyContain(f => f.PersonId == null);
    }

    [Fact]
    public async Task Removing_a_person_leaves_a_rejection_standing()
    {
        // The person is going away, but "this face is not that person" was still a judgement the
        // user made, and nothing here contradicts it.
        var alice = await AddPersonAsync("Alice");
        var rejected = await AddFaceAsync(null, @"D:\photos\1.jpg", Assignment.Rejected);
        await AddFaceAsync(alice, @"D:\photos\2.jpg");

        await _subject.DeleteAsync(alice, Detector, default);

        var all = new List<Face>();
        await foreach (var face in _faces.StreamByDetectorAsync(Detector, activeOnly: true, default))
        {
            all.Add(face);
        }

        all.Single(f => f.Id == rejected).Assignment.Should().Be(Assignment.Rejected);
    }

    [Fact]
    public async Task Removing_a_person_frees_their_groups_to_be_named_again()
    {
        var alice = await AddPersonAsync("Alice");
        var cluster = new ClusterRecord(
            ClusterId.New(), Detector, "embedder", 3, FaceId.New(), DateTimeOffset.UnixEpoch, alice);
        await _clusters.ReplaceAllAsync(Detector, "embedder", [cluster], default);

        await _subject.DeleteAsync(alice, Detector, default);

        (await _clusters.GetByIdAsync(cluster.Id, default))!.Value.PersonId.Should().BeNull(
            "the same faces should be immediately available to name correctly");
    }

    [Fact]
    public async Task Removing_photographs_from_a_person_detaches_them()
    {
        var alice = await AddPersonAsync("Alice");
        var wrong = await AddFaceAsync(alice, @"D:\photos\1.jpg");
        await AddFaceAsync(alice, @"D:\photos\2.jpg");

        var result = await _subject.RemoveFacesAsync(alice, [wrong], default);

        result.IsSuccess.Should().BeTrue();
        (await _subject.GetPhotosAsync(alice, Detector, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Several_photographs_can_be_removed_at_once()
    {
        var alice = await AddPersonAsync("Alice");
        var first = await AddFaceAsync(alice, @"D:\photos\1.jpg");
        var second = await AddFaceAsync(alice, @"D:\photos\2.jpg");
        await AddFaceAsync(alice, @"D:\photos\3.jpg");

        await _subject.RemoveFacesAsync(alice, [first, second], default);

        (await _subject.GetPhotosAsync(alice, Detector, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_removed_photograph_is_recorded_as_rejected_rather_than_merely_cleared()
    {
        // The difference decides whether the correction survives. A cleared face looks identical
        // to one never grouped, so the next grouping would put it straight back and the user would
        // have to remove it again after every run.
        var alice = await AddPersonAsync("Alice");
        var wrong = await AddFaceAsync(alice, @"D:\photos\1.jpg");

        await _subject.RemoveFacesAsync(alice, [wrong], default);

        var all = new List<Face>();
        await foreach (var face in _faces.StreamByDetectorAsync(Detector, activeOnly: true, default))
        {
            all.Add(face);
        }

        var removed = all.Single(f => f.Id == wrong);
        removed.Assignment.Should().Be(Assignment.Rejected);
        removed.IsUserDecided.Should().BeTrue("naming must not quietly reverse this");
    }

    [Fact]
    public async Task Removing_nothing_is_refused_rather_than_silently_doing_nothing()
    {
        var alice = await AddPersonAsync("Alice");

        (await _subject.RemoveFacesAsync(alice, [], default)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Photographs_can_be_moved_to_another_person()
    {
        // The correction for a face grouping put on the wrong person, as distinct from one who is
        // nobody in this library. Removing would only detach it; moving says where it belongs.
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");
        var misfiled = await AddFaceAsync(alice, @"D:\photos\1.jpg");
        await AddFaceAsync(alice, @"D:\photos\2.jpg");

        var result = await _subject.MoveFacesAsync(
            alice, bob, [misfiled], Detector, "embedder", default);

        result.IsSuccess.Should().BeTrue();
        (await _subject.GetPhotosAsync(alice, Detector, default)).Should().ContainSingle();
        (await _subject.GetPhotosAsync(bob, Detector, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_moved_photograph_is_recorded_as_confirmed()
    {
        // Somebody who looked at a photograph and said whose it is has made a judgement, and the
        // next grouping run must not quietly move it back.
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");
        var moved = await AddFaceAsync(alice, @"D:\photos\1.jpg");

        await _subject.MoveFacesAsync(alice, bob, [moved], Detector, "embedder", default);

        var theirs = await _subject.GetPhotosAsync(bob, Detector, default);
        theirs.Single().Assignment.Should().Be(Assignment.Confirmed);
    }

    [Fact]
    public async Task Several_photographs_move_together()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");
        var first = await AddFaceAsync(alice, @"D:\photos\1.jpg");
        var second = await AddFaceAsync(alice, @"D:\photos\2.jpg");
        await AddFaceAsync(alice, @"D:\photos\3.jpg");

        await _subject.MoveFacesAsync(alice, bob, [first, second], Detector, "embedder", default);

        (await _subject.GetPhotosAsync(bob, Detector, default)).Should().HaveCount(2);
        (await _subject.GetPhotosAsync(alice, Detector, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Moving_to_the_same_person_is_refused()
    {
        var alice = await AddPersonAsync("Alice");
        var face = await AddFaceAsync(alice, @"D:\photos\1.jpg");

        (await _subject.MoveFacesAsync(alice, alice, [face], Detector, "embedder", default))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Moving_nothing_is_refused()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        (await _subject.MoveFacesAsync(alice, bob, [], Detector, "embedder", default))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task The_person_being_looked_at_is_not_offered_as_a_destination()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPersonAsync("Bob");
        await AddPersonAsync("Carol");

        var others = await _subject.GetOtherPeopleAsync(alice, default);

        others.Should().HaveCount(2);
        others.Should().NotContain(p => p.Id == alice);
    }

    [Fact]
    public async Task Acting_on_a_person_who_no_longer_exists_fails_cleanly()
    {
        var vanished = PersonId.New();

        (await _subject.RenameAsync(vanished, "Alice", default)).IsSuccess.Should().BeFalse();
        (await _subject.DeleteAsync(vanished, Detector, default)).IsSuccess.Should().BeFalse();
        (await _subject.RemoveFacesAsync(vanished, [FaceId.New()], default)).IsSuccess.Should().BeFalse();
    }

    public void Dispose() => _database.Dispose();
}
