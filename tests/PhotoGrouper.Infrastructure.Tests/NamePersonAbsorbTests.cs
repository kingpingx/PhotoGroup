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
/// Covers what naming somebody does to the groups that were not named.
/// </summary>
/// <remarks>
/// Grouping routinely splits one person into several groups: different lighting, a different
/// haircut, ten years apart. Naming one of those used to say nothing about the others, so the user
/// was asked to name the same person again and again, and only a complete re-grouping would join
/// them up. Naming is a statement about who somebody is, not about one group of pixels, so the
/// remaining groups are checked against them immediately.
/// </remarks>
public sealed class NamePersonAbsorbTests : IDisposable
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

    public NamePersonAbsorbTests()
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

    /// <summary>
    /// A unit vector pointing in a chosen direction, nudged by a small amount.
    /// </summary>
    /// <remarks>
    /// Two faces of one person differ slightly; two people differ a great deal. Building the vectors
    /// directly keeps these tests about the matching rule rather than about any particular model.
    /// </remarks>
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

    /// <summary>
    /// Declares a group holding the given face vectors. Nothing is written until
    /// <see cref="CommitAsync"/> runs.
    /// </summary>
    /// <remarks>
    /// Deferred because writing the groups one at a time does not work: replacing the cluster set
    /// deletes the existing rows, and a face's cluster reference is cleared when its cluster goes,
    /// so each new group would silently empty the ones before it. Production writes every group in
    /// a single pass for the same reason, and building the fixture the same way keeps the test
    /// honest about the shape of the real thing.
    /// </remarks>
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

    /// <summary>Writes every declared group and its membership, as a grouping run would.</summary>
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

    private async Task<int> PhotoCountAsync(string name)
    {
        var person = await _people.GetByNameAsync(PersonName.Create(name), default);
        return person is null ? 0 : (await _faces.GetByPersonAsync(person.Id, Detector, default)).Count;
    }

    [Fact]
    public async Task Naming_a_group_also_claims_another_group_of_the_same_person()
    {
        // The reported problem: two groups of one person, one named, and the other still asking for
        // a name as though nothing had been said.
        var first = await AddGroupAsync(Vector(3), Vector(3, 0.05f));
        await AddGroupAsync(Vector(3, 0.10f), Vector(3, 0.12f));

        await CommitAsync();

        var result = await _subject.ExecuteAsync(first, "Alice", default);

        result.IsSuccess.Should().BeTrue();
        result.GroupsAbsorbed.Should().Be(1);
        (await PhotoCountAsync("Alice")).Should().Be(4);
    }

    [Fact]
    public async Task The_absorbed_group_stops_asking_for_a_name()
    {
        var first = await AddGroupAsync(Vector(3), Vector(3, 0.05f));
        await AddGroupAsync(Vector(3, 0.10f), Vector(3, 0.12f));

        await CommitAsync();

        await _subject.ExecuteAsync(first, "Alice", default);

        var unnamed = (await _clusters.GetAllAsync(Detector, Embedder, default))
            .Where(c => c.PersonId is null)
            .ToList();

        unnamed.Should().BeEmpty("both groups are the same person and one of them has been named");
    }

    [Fact]
    public async Task A_different_person_is_left_alone()
    {
        // The failure that would matter most: quietly filing a stranger's photographs under
        // somebody's name. Being too cautious merely leaves a group waiting to be named.
        var alice = await AddGroupAsync(Vector(3), Vector(3, 0.05f));
        await AddGroupAsync(Vector(40), Vector(40, 0.05f));

        await CommitAsync();

        var result = await _subject.ExecuteAsync(alice, "Alice", default);

        result.GroupsAbsorbed.Should().Be(0);
        (await PhotoCountAsync("Alice")).Should().Be(2);
    }

    [Fact]
    public async Task Several_matching_groups_are_all_claimed_at_once()
    {
        var first = await AddGroupAsync(Vector(7), Vector(7, 0.04f));
        await AddGroupAsync(Vector(7, 0.08f), Vector(7, 0.09f));
        await AddGroupAsync(Vector(7, 0.11f), Vector(7, 0.12f));

        await CommitAsync();

        var result = await _subject.ExecuteAsync(first, "Bob", default);

        result.GroupsAbsorbed.Should().Be(2);
        (await PhotoCountAsync("Bob")).Should().Be(6);
    }

    [Fact]
    public async Task Naming_the_only_group_absorbs_nothing_and_still_succeeds()
    {
        var only = await AddGroupAsync(Vector(11), Vector(11, 0.03f));
        await CommitAsync();

        var result = await _subject.ExecuteAsync(only, "Solo", default);

        result.IsSuccess.Should().BeTrue();
        result.GroupsAbsorbed.Should().Be(0);
        (await PhotoCountAsync("Solo")).Should().Be(2);
    }

    [Fact]
    public async Task An_already_named_group_is_not_stolen()
    {
        // Naming Bob must not take groups that are already Alice's, however similar they look:
        // that would move photographs away from a name the user had deliberately given.
        var alice = await AddGroupAsync(Vector(5), Vector(5, 0.04f));
        var bobs = await AddGroupAsync(Vector(5, 0.08f), Vector(5, 0.09f));
        await CommitAsync();

        await _subject.ExecuteAsync(alice, "Alice", default);
        var result = await _subject.ExecuteAsync(bobs, "Bob", default);

        result.GroupsAbsorbed.Should().Be(0);
        (await PhotoCountAsync("Alice")).Should().Be(2);
        (await PhotoCountAsync("Bob")).Should().Be(2);
    }

    public void Dispose() => _database.Dispose();
}
