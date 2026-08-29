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
/// Covers giving every waiting group a placeholder name.
/// </summary>
/// <remarks>
/// The failure to guard against is producing two placeholders for one person. Naming a group also
/// claims the other groups that look like the same person, so a list of unnamed groups read once
/// at the start goes stale as the run proceeds; naming from that stale list would split somebody
/// across "Person 3" and "Person 7" and leave the user to find and merge them by hand.
/// </remarks>
public sealed class AutoNameGroupsTests : IDisposable
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
    private readonly AutoNameGroupsUseCase _subject;

    public AutoNameGroupsTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _clusters = new SqliteClusterRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);

        var naming = new NamePersonUseCase(_people, _clusters, _faces, _embeddings, new SystemClock());
        _subject = new AutoNameGroupsUseCase(_people, _clusters, naming);
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static float[] Vector(int direction, float nudge = 0f)
    {
        var v = new float[Dimensions];
        v[direction] = 1f;

        if (nudge > 0)
        {
            v[(direction + 1) % Dimensions] = nudge;
        }

        var length = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / length)];
    }

    private readonly List<(ClusterId Id, List<Face> Faces)> _pending = [];

    private async Task<ClusterId> AddGroupAsync(params float[][] vectors)
    {
        var clusterId = ClusterId.New();
        var faces = new List<Face>();
        var embeddings = new List<FaceEmbedding>();

        foreach (var vector in vectors)
        {
            var photo = new Photo(
                PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
            await _photos.UpsertAsync(photo, default);

            var face = new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks);

            faces.Add(face);
            embeddings.Add(new FaceEmbedding(face.Id, vector));
        }

        await _faces.BulkInsertAsync(faces, default);
        await _embeddings.BulkUpsertAsync(Embedder, "1", embeddings, default);

        _pending.Add((clusterId, faces));
        return clusterId;
    }

    /// <remarks>
    /// Every group is written in one pass, as a grouping run does. Writing them one at a time
    /// replaces the cluster set each time and silently empties the groups written before it.
    /// </remarks>
    private async Task CommitAsync()
    {
        await _clusters.ReplaceAllAsync(Detector, Embedder,
            [.. _pending.Select(g => new ClusterRecord(
                g.Id, Detector, Embedder, g.Faces.Count, g.Faces[0].Id, DateTimeOffset.UnixEpoch))],
            default);

        await _faces.SetClustersAsync(
            [.. _pending.SelectMany(g => g.Faces.Select(f => new FaceClusterAssignment(f.Id, g.Id)))],
            default);
    }

    private async Task<List<string>> NamesAsync() =>
        [.. (await _people.GetAllAsync(default)).Select(p => p.Name.Value)];

    [Fact]
    public async Task Every_waiting_group_gets_a_name()
    {
        await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await AddGroupAsync(Vector(20), Vector(20, 0.04f));
        await AddGroupAsync(Vector(40), Vector(40, 0.04f));
        await CommitAsync();

        var result = await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        result.IsSuccess.Should().BeTrue();
        result.Named.Should().Be(3);
        (await NamesAsync()).Should().BeEquivalentTo(["Person 1", "Person 2", "Person 3"]);
    }

    /// <remarks>
    /// The numbers are not decoration: the low ones should belong to the people this library is
    /// actually about, which is the same order somebody would have named them by hand.
    /// </remarks>
    [Fact]
    public async Task The_largest_group_is_numbered_first()
    {
        await AddGroupAsync(Vector(20));
        await AddGroupAsync(Vector(3), Vector(3, 0.04f), Vector(3, 0.05f));
        await CommitAsync();

        await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        var biggest = await _people.GetByNameAsync(PersonName.Create("Person 1"), default);
        (await _faces.GetByPersonAsync(biggest!.Id, Detector, default))
            .Should().HaveCount(3, "the group with the most faces is numbered first");
    }

    /// <remarks>
    /// The defect this rules out: naming from a list read once at the start. The second group is
    /// the same person as the first, so naming the first claims it; naming it again would create a
    /// second placeholder for one person.
    /// </remarks>
    [Fact]
    public async Task A_group_claimed_by_an_earlier_name_is_not_named_again()
    {
        await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await AddGroupAsync(Vector(3, 0.08f), Vector(3, 0.09f));
        await CommitAsync();

        var result = await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        result.Named.Should().Be(1, "both groups are one person, so one name covers them");
        (await NamesAsync()).Should().BeEquivalentTo(["Person 1"]);

        var person = await _people.GetByNameAsync(PersonName.Create("Person 1"), default);
        (await _faces.GetByPersonAsync(person!.Id, Detector, default)).Should().HaveCount(4);
    }

    [Fact]
    public async Task Names_already_in_use_are_not_reused()
    {
        await _people.AddAsync(
            new Person(PersonId.New(), PersonName.Create("Person 1"), DateTimeOffset.UnixEpoch), default);

        await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await CommitAsync();

        await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        (await NamesAsync()).Should().BeEquivalentTo(["Person 1", "Person 2"],
            "reusing the name would merge a stranger into somebody who already exists");
    }

    [Fact]
    public async Task Groups_already_named_are_left_alone()
    {
        var alice = await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await AddGroupAsync(Vector(40), Vector(40, 0.04f));
        await CommitAsync();

        var naming = new NamePersonUseCase(_people, _clusters, _faces, _embeddings, new SystemClock());
        await naming.ExecuteAsync(alice, "Alice", default);

        var result = await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        result.Named.Should().Be(1);
        (await NamesAsync()).Should().BeEquivalentTo(["Alice", "Person 1"]);
    }

    /// <remarks>
    /// A group whose faces have all been placed by hand cannot be named, which is a correct
    /// refusal rather than a failure here. The number it would have taken goes to the next group,
    /// so the placeholders stay consecutive.
    /// </remarks>
    [Fact]
    public async Task A_group_that_cannot_be_named_is_skipped_without_consuming_a_number()
    {
        var spokenFor = await AddGroupAsync(Vector(20));
        await AddGroupAsync(Vector(40), Vector(40, 0.04f));
        await CommitAsync();

        var owner = new Person(PersonId.New(), PersonName.Create("Alice"), DateTimeOffset.UnixEpoch);
        await _people.AddAsync(owner, default);
        await _faces.AssignAsync(
            [.. _pending.First(g => g.Id == spokenFor).Faces
                .Select(f => new FaceAssignment(f.Id, owner.Id, Assignment.Confirmed))],
            default);

        var result = await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        result.Named.Should().Be(1);
        result.Skipped.Should().Be(1);
        (await NamesAsync()).Should().BeEquivalentTo(["Alice", "Person 1"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_stem_is_refused(string prefix)
    {
        await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await CommitAsync();

        var result = await _subject.ExecuteAsync(prefix, Detector, Embedder, default);

        result.IsSuccess.Should().BeFalse();
        (await NamesAsync()).Should().BeEmpty("nothing is created when the stem is unusable");
    }

    [Fact]
    public async Task The_stem_is_the_caller_s_choice()
    {
        await AddGroupAsync(Vector(3), Vector(3, 0.04f));
        await CommitAsync();

        await _subject.ExecuteAsync("Guest", Detector, Embedder, default);

        (await NamesAsync()).Should().BeEquivalentTo(["Guest 1"]);
    }

    [Fact]
    public async Task Nothing_waiting_means_nothing_happens()
    {
        var result = await _subject.ExecuteAsync("Person", Detector, Embedder, default);

        result.IsSuccess.Should().BeTrue();
        result.Named.Should().Be(0);
        (await NamesAsync()).Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();
}
