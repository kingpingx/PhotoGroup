using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Application.UseCases;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.FileSystem;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers naming a group whose faces have already been placed by hand.
/// </summary>
/// <remarks>
/// Naming used to create the person, point the group at them, assign nothing and report success,
/// leaving a name with no photographs behind it and no indication of why. It is reachable by an
/// ordinary sequence: moving a photograph to somebody else marks that face as decided but leaves
/// its group unnamed, so the group comes back round as one to name.
///
/// The rule that produced it is worth keeping — a decision made by hand is not overturned by a
/// later automatic pass — so the fix is to notice before writing anything, and say so.
/// </remarks>
public sealed class NamePersonEmptyGroupTests : IDisposable
{
    private const string Detector = "test.detector";
    private const string Embedder = "test.embedder";
    private const int Dimensions = 64;

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteClusterRepository _clusters;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly NamePersonUseCase _subject;

    public NamePersonEmptyGroupTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _clusters = new SqliteClusterRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);
        _subject = new NamePersonUseCase(_people, _clusters, _faces, _embeddings, new SystemClock());
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static float[] Vector(int direction)
    {
        var v = new float[Dimensions];
        v[direction] = 1f;
        return v;
    }

    /// <summary>Writes one group of the given size and returns it with its faces.</summary>
    private async Task<(ClusterId Id, List<Face> Faces)> AddGroupAsync(int size)
    {
        var clusterId = ClusterId.New();
        var faces = new List<Face>();
        var embeddings = new List<FaceEmbedding>();

        for (var i = 0; i < size; i++)
        {
            var photo = new Photo(
                PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
            await _photos.UpsertAsync(photo, default);

            var face = new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks);

            faces.Add(face);
            embeddings.Add(new FaceEmbedding(face.Id, Vector(3)));
        }

        await _faces.BulkInsertAsync(faces, default);
        await _embeddings.BulkUpsertAsync(Embedder, "1", embeddings, default);

        await _clusters.ReplaceAllAsync(Detector, Embedder,
            [new ClusterRecord(clusterId, Detector, Embedder, size, faces[0].Id, DateTimeOffset.UnixEpoch)],
            default);

        await _faces.SetClustersAsync(
            [.. faces.Select(f => new FaceClusterAssignment(f.Id, clusterId))], default);

        return (clusterId, faces);
    }

    /// <summary>Somebody who already owns a face, as moving a photograph to them would leave it.</summary>
    private async Task<Person> GiveToNewPersonAsync(string name, params Face[] faces)
    {
        var person = new Person(PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch);
        await _people.AddAsync(person, default);

        await _faces.AssignAsync(
            [.. faces.Select(f => new FaceAssignment(f.Id, person.Id, Assignment.Confirmed))], default);

        return person;
    }

    [Fact]
    public async Task Naming_a_group_that_is_entirely_spoken_for_creates_nobody()
    {
        var group = await AddGroupAsync(1);
        await GiveToNewPersonAsync("Alice", group.Faces[0]);

        var result = await _subject.ExecuteAsync(group.Id, "Bob", default);

        result.IsSuccess.Should().BeFalse();
        (await _people.GetByNameAsync(PersonName.Create("Bob"), default))
            .Should().BeNull("a name with no photographs behind it is not a person");
    }

    [Fact]
    public async Task The_refusal_says_who_already_has_the_face()
    {
        var group = await AddGroupAsync(1);
        await GiveToNewPersonAsync("Alice", group.Faces[0]);

        var result = await _subject.ExecuteAsync(group.Id, "Bob", default);

        result.Error.Should().Contain("Alice", "the user needs to know where to go and undo it");
    }

    /// <remarks>
    /// The group must be left exactly as it was. Pointing it at a person it gave nothing to is how
    /// the original defect hid itself: the group vanished from the screen, so the user saw a name
    /// appear and their face disappear, with nothing left to click on.
    /// </remarks>
    [Fact]
    public async Task A_refused_group_keeps_its_face_and_stays_available()
    {
        var group = await AddGroupAsync(1);
        var alice = await GiveToNewPersonAsync("Alice", group.Faces[0]);

        await _subject.ExecuteAsync(group.Id, "Bob", default);

        (await _faces.GetByPersonAsync(alice.Id, Detector, default))
            .Should().HaveCount(1, "the face still belongs to whoever it was given to");

        var stored = await _clusters.GetByIdAsync(group.Id, default);
        stored!.Value.PersonId
            .Should().BeNull("the group was not named, so it must still be offered");
    }

    /// <remarks>
    /// The partial case still works, and is the reason this is not simply a ban on groups holding a
    /// decided face: a group of four where one has been moved away should still name the other three.
    /// </remarks>
    [Fact]
    public async Task A_group_with_one_face_spoken_for_names_the_rest()
    {
        var group = await AddGroupAsync(3);
        await GiveToNewPersonAsync("Alice", group.Faces[0]);

        var result = await _subject.ExecuteAsync(group.Id, "Bob", default);

        result.IsSuccess.Should().BeTrue();

        var bob = await _people.GetByNameAsync(PersonName.Create("Bob"), default);
        (await _faces.GetByPersonAsync(bob!.Id, Detector, default)).Should().HaveCount(2);
    }

    /// <remarks>
    /// The count shown to the user is what was actually assigned. Reporting the group's size told
    /// somebody they had gained photographs that had gone nowhere.
    /// </remarks>
    [Fact]
    public async Task The_reported_count_is_what_was_actually_assigned()
    {
        var group = await AddGroupAsync(3);
        await GiveToNewPersonAsync("Alice", group.Faces[0]);

        var result = await _subject.ExecuteAsync(group.Id, "Bob", default);

        result.FacesAssigned.Should().Be(2);
    }

    public void Dispose() => _database.Dispose();
}
