using FluentAssertions;
using PhotoGrouper.Application.People;
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
/// Covers folding several names for one person into one.
/// </summary>
/// <remarks>
/// Merging destroys a name, which is the one thing in this library nothing can regenerate, so the
/// tests are about what must survive it: every photograph must end up under the surviving name, the
/// merged person's groups must follow rather than being released, and a face the user had rejected
/// must stay rejected.
///
/// The group behaviour is the subtle one and the reason this is not assembled from the operations
/// that already exist. Moving the faces and then deleting the emptied person detaches that person's
/// groups, and a detached group comes straight back to the People screen asking to be named — so
/// merging two people would immediately offer the user a third.
/// </remarks>
public sealed class MergePeopleTests : IDisposable
{
    private const string Detector = "test.detector";
    private const string Embedder = "test.embedder";
    private const int Dimensions = 32;

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteClusterRepository _clusters;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly MergePeopleUseCase _subject;

    public MergePeopleTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _clusters = new SqliteClusterRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);

        _subject = new MergePeopleUseCase(_people, _faces, _clusters, Calibrator());
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static float[] Vector(int direction)
    {
        var v = new float[Dimensions];
        v[direction % Dimensions] = 1f;
        return v;
    }

    private readonly List<(ClusterId Id, List<Face> Faces)> _pending = [];

    /// <summary>A named person holding one group of the given size.</summary>
    private async Task<(PersonId Id, ClusterId Cluster, List<Face> Faces)> AddPersonAsync(
        string name, int faceCount, int direction)
    {
        var person = new Person(
            PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch,
            centroid: Vector(direction));

        await _people.AddAsync(person, default);

        var clusterId = ClusterId.New();
        var faces = new List<Face>();
        var vectors = new List<FaceEmbedding>();

        for (var i = 0; i < faceCount; i++)
        {
            var photo = new Photo(
                PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
            await _photos.UpsertAsync(photo, default);

            var face = new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks);

            faces.Add(face);
            vectors.Add(new FaceEmbedding(face.Id, Vector(direction)));
        }

        await _faces.BulkInsertAsync(faces, default);
        await _embeddings.BulkUpsertAsync(Embedder, "1", vectors, default);

        _pending.Add((clusterId, faces));
        return (person.Id, clusterId, faces);
    }

    /// <summary>Writes every declared group at once, as a grouping run does, and names them.</summary>
    private async Task CommitAsync(params (PersonId Person, ClusterId Cluster)[] ownership)
    {
        await _clusters.ReplaceAllAsync(Detector, Embedder,
            [.. _pending.Select(g => new ClusterRecord(
                g.Id, Detector, Embedder, g.Faces.Count, g.Faces[0].Id, DateTimeOffset.UnixEpoch))],
            default);

        await _faces.SetClustersAsync(
            [.. _pending.SelectMany(g => g.Faces.Select(f => new FaceClusterAssignment(f.Id, g.Id)))],
            default);

        foreach (var (person, cluster) in ownership)
        {
            await _clusters.SetPersonAsync(cluster, person, default);

            var members = await _faces.GetByClusterAsync(cluster, default);
            await _faces.AssignAsync(
                [.. members.Select(f => new FaceAssignment(f.Id, person, Assignment.Auto))], default);
        }
    }

    private Task<MergeResult> MergeAsync(PersonId keep, params PersonId[] others) =>
        _subject.ExecuteAsync(keep, others, Detector, Embedder, default);

    [Fact]
    public async Task Every_photograph_ends_up_under_the_surviving_name()
    {
        var alice = await AddPersonAsync("Alice", 4, 3);
        var duplicate = await AddPersonAsync("Alice 2", 2, 3);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        var result = await MergeAsync(alice.Id, duplicate.Id);

        result.IsSuccess.Should().BeTrue();
        result.MergedPeople.Should().Be(1);
        result.MovedFaces.Should().Be(2);

        (await _faces.GetByPersonAsync(alice.Id, Detector, default)).Should().HaveCount(6);
        (await _people.GetByIdAsync(duplicate.Id, default)).Should().BeNull();
    }

    /// <remarks>
    /// The defect this rules out, and the reason merging is its own operation. Releasing the merged
    /// person's group puts it back on the People screen as something new to name, so the user
    /// merges two people and is immediately offered a third.
    /// </remarks>
    [Fact]
    public async Task The_merged_persons_group_follows_them_rather_than_becoming_unnamed()
    {
        var alice = await AddPersonAsync("Alice", 2, 3);
        var duplicate = await AddPersonAsync("Alice 2", 2, 3);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        await MergeAsync(alice.Id, duplicate.Id);

        var unnamed = (await _clusters.GetAllAsync(Detector, Embedder, default))
            .Where(c => c.PersonId is null)
            .ToList();

        unnamed.Should().BeEmpty("a merge must not leave a group asking to be named");

        (await _clusters.GetByIdAsync(duplicate.Cluster, default))!.Value.PersonId
            .Should().Be(alice.Id);
    }

    [Fact]
    public async Task Several_names_merge_at_once()
    {
        var alice = await AddPersonAsync("Alice", 3, 3);
        var second = await AddPersonAsync("Alice 2", 1, 3);
        var third = await AddPersonAsync("7", 2, 3);
        await CommitAsync((alice.Id, alice.Cluster), (second.Id, second.Cluster), (third.Id, third.Cluster));

        var result = await MergeAsync(alice.Id, second.Id, third.Id);

        result.MergedPeople.Should().Be(2);
        (await _faces.GetByPersonAsync(alice.Id, Detector, default)).Should().HaveCount(6);
        (await _people.GetAllAsync(default)).Should().ContainSingle().Which.Name.Value.Should().Be("Alice");
    }

    /// <remarks>
    /// The user said that face is not this person. Merging two names is a statement about the names,
    /// not about that judgement, so the rejection outlives it.
    /// </remarks>
    [Fact]
    public async Task A_rejected_face_is_not_dragged_into_the_merge()
    {
        var alice = await AddPersonAsync("Alice", 2, 3);
        var duplicate = await AddPersonAsync("Alice 2", 2, 3);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        var rejected = duplicate.Faces[0];
        await _faces.AssignAsync(
            [new FaceAssignment(rejected.Id, null, Assignment.Rejected)], default);

        var result = await MergeAsync(alice.Id, duplicate.Id);

        result.MovedFaces.Should().Be(1, "only the face that was not rejected moves");

        var mine = await _faces.GetByPersonAsync(alice.Id, Detector, default);
        mine.Should().HaveCount(3);
        mine.Should().NotContain(f => f.Id == rejected.Id);
    }

    /// <remarks>
    /// Recorded as decided by hand, because it was: somebody looked at two faces and said they are
    /// one person. The next automatic run must not quietly undo that.
    /// </remarks>
    [Fact]
    public async Task Merged_faces_are_marked_as_the_users_own_decision()
    {
        var alice = await AddPersonAsync("Alice", 1, 3);
        var duplicate = await AddPersonAsync("Alice 2", 1, 3);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        await MergeAsync(alice.Id, duplicate.Id);

        var moved = (await _faces.GetByPersonAsync(alice.Id, Detector, default))
            .Single(f => f.Id == duplicate.Faces[0].Id);

        moved.Assignment.Should().Be(Assignment.Confirmed);
    }

    /// <remarks>
    /// The average is what the next grouping run compares new faces against. Left describing only
    /// the surviving person's original photographs, it would keep failing to recognise the ones
    /// that had just been merged in.
    /// </remarks>
    [Fact]
    public async Task The_surviving_persons_average_covers_everything_they_now_hold()
    {
        var alice = await AddPersonAsync("Alice", 1, 3);
        var duplicate = await AddPersonAsync("Alice 2", 1, 5);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        await MergeAsync(alice.Id, duplicate.Id);

        var centroid = (await _people.GetByIdAsync(alice.Id, default))!.Centroid!;

        // Both directions now contribute, which they cannot if the average was left alone.
        centroid[3].Should().BeGreaterThan(0.5f);
        centroid[5].Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public async Task Merging_a_person_into_themselves_is_refused()
    {
        var alice = await AddPersonAsync("Alice", 2, 3);
        await CommitAsync((alice.Id, alice.Cluster));

        var result = await MergeAsync(alice.Id, alice.Id);

        result.IsSuccess.Should().BeFalse();
        (await _people.GetByIdAsync(alice.Id, default)).Should().NotBeNull();
        (await _faces.GetByPersonAsync(alice.Id, Detector, default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Merging_into_somebody_who_has_gone_is_refused()
    {
        var alice = await AddPersonAsync("Alice", 1, 3);
        var duplicate = await AddPersonAsync("Alice 2", 1, 3);
        await CommitAsync((alice.Id, alice.Cluster), (duplicate.Id, duplicate.Cluster));

        await _people.RemoveAsync(alice.Id, default);

        var result = await MergeAsync(alice.Id, duplicate.Id);

        result.IsSuccess.Should().BeFalse();
        (await _people.GetByIdAsync(duplicate.Id, default))
            .Should().NotBeNull("nothing is destroyed when the target is gone");
    }

    /// <summary>The shared calibrator, over this test's own repositories.</summary>
    private PersonCalibrator Calibrator() => new(_people, _faces, _embeddings);

    public void Dispose() => _database.Dispose();
}
