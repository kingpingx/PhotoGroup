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
/// Covers rebuilding the values nobody typed, for a library where they have drifted.
/// </summary>
/// <remarks>
/// Every test here reproduces a drift that a real library really accumulates, then asserts it is
/// gone. The two that matter most are the ones the application still causes: a person's average is
/// not recomputed when a photograph is deleted and its faces cascade away, which is exactly what
/// removing duplicate photographs does; and a group's recorded size has never been corrected after
/// its first grouping run, though it decides the order groups appear in, whether a group is large
/// enough to be offered for naming, and the totals in the header.
///
/// Idempotence gets its own test, because it is what makes this safe to offer as a button rather
/// than a migration: pressing it twice must be indistinguishable from pressing it once.
/// </remarks>
public sealed class RepairDerivedDataTests : IDisposable
{
    private const string Detector = "test.detector";
    private const string Embedder = "test.embedder";

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteClusterRepository _clusters;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly RepairDerivedDataUseCase _subject;

    public RepairDerivedDataTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _clusters = new SqliteClusterRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);

        _subject = new RepairDerivedDataUseCase(
            _people, _faces, _clusters, _embeddings,
            new PersonCalibrator(_people, _faces, _embeddings));
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static float[] Unit(int axis)
    {
        var v = new float[8];
        v[axis] = 1f;
        return v;
    }

    private async Task<PersonId> AddPersonAsync(
        string name, FaceId? cover = null, float[]? centroid = null)
    {
        var person = new Person(
            PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch, cover, centroid);

        await _people.AddAsync(person, default);
        return person.Id;
    }

    private async Task<(FaceId Face, PhotoId Photo)> AddFaceAsync(
        PersonId? owner, int axis = 0, int facePixels = 100)
    {
        var photo = new Photo(
            PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var face = new Face(
            FaceId.New(), photo.Id, Detector, "1",
            new FaceBox(0, 0, facePixels, facePixels, 0.9f), Landmarks, personId: owner);

        await _faces.BulkInsertAsync([face], default);
        await _embeddings.BulkUpsertAsync(
            Embedder, "1", [new FaceEmbedding(face.Id, Unit(axis))], default);

        return (face.Id, photo.Id);
    }

    /// <summary>Writes one group holding the given faces, with a size the caller chooses.</summary>
    private async Task<ClusterId> AddClusterAsync(
        IReadOnlyList<FaceId> members, int recordedSize, FaceId? recordedMedoid = null)
    {
        var clusterId = ClusterId.New();

        await _clusters.ReplaceAllAsync(Detector, Embedder,
            [new ClusterRecord(
                clusterId, Detector, Embedder, recordedSize,
                recordedMedoid ?? members[0], DateTimeOffset.UnixEpoch)],
            default);

        await _faces.SetClustersAsync(
            [.. members.Select(id => new FaceClusterAssignment(id, clusterId))], default);

        return clusterId;
    }

    private Task<RepairResult> RepairAsync() =>
        _subject.ExecuteAsync(Detector, Embedder, NullProgress.Instance, default);

    /// <remarks>
    /// The exact state removing duplicate photographs leaves behind: the photo row is deleted, its
    /// faces cascade away, and nothing tells the person who held them.
    /// </remarks>
    [Fact]
    public async Task A_person_whose_photographs_were_deleted_gets_a_correct_average()
    {
        var alice = await AddPersonAsync("Alice");
        await AddFaceAsync(alice, axis: 0);
        var doomed = await AddFaceAsync(alice, axis: 1);

        // Bring them up to date, then delete a photograph behind their back.
        await RepairAsync();
        await _photos.RemoveAsync(doomed.Photo, default);

        await RepairAsync();

        var centroid = (await _people.GetByIdAsync(alice, default))!.Centroid!;

        centroid[0].Should().BeApproximately(1f, 0.001f);
        centroid[1].Should().BeApproximately(0f, 0.001f,
            "the deleted photograph must stop contributing to who they are");
    }

    [Fact]
    public async Task A_person_left_with_nothing_stops_claiming_an_average()
    {
        var alice = await AddPersonAsync("Alice", centroid: Unit(3));

        var result = await RepairAsync();

        result.AveragesCleared.Should().Be(1);
        (await _people.GetByIdAsync(alice, default))!.Centroid.Should().BeNull();
    }

    /// <remarks>
    /// The column carries no foreign key, so a cover pointing at a face that no longer exists
    /// dangles indefinitely and only the render-time fallback hides it.
    /// </remarks>
    [Fact]
    public async Task A_cover_face_that_no_longer_exists_is_replaced()
    {
        var alice = await AddPersonAsync("Alice", cover: FaceId.New());
        var theirs = await AddFaceAsync(alice);

        var result = await RepairAsync();

        result.CoversRepaired.Should().Be(1);
        (await _people.GetByIdAsync(alice, default))!.CoverFaceId.Should().Be(theirs.Face);
    }

    /// <remarks>
    /// The number that decides how groups are ordered, whether one is large enough to be offered
    /// for naming, and the totals in the header — and the only thing that ever wrote it was a full
    /// grouping run.
    /// </remarks>
    [Fact]
    public async Task A_group_that_lost_photographs_reports_its_real_size()
    {
        var first = await AddFaceAsync(null);
        var second = await AddFaceAsync(null);
        var cluster = await AddClusterAsync([first.Face, second.Face], recordedSize: 8);

        var result = await RepairAsync();

        result.GroupsResized.Should().Be(1);
        (await _clusters.GetByIdAsync(cluster, default))!.Value.Size.Should().Be(2);
    }

    [Fact]
    public async Task A_group_with_nothing_left_in_it_is_removed()
    {
        var lonely = await AddFaceAsync(null);
        var cluster = await AddClusterAsync([lonely.Face], recordedSize: 1);

        await _photos.RemoveAsync(lonely.Photo, default);

        var result = await RepairAsync();

        result.EmptyGroupsRemoved.Should().Be(1);
        (await _clusters.GetByIdAsync(cluster, default)).Should().BeNull(
            "a zero-size row still comes back on every refresh and cannot usefully be named");
    }

    [Fact]
    public async Task A_group_whose_central_face_is_gone_gets_a_new_one()
    {
        var kept = await AddFaceAsync(null, axis: 0);
        var other = await AddFaceAsync(null, axis: 0);
        var cluster = await AddClusterAsync(
            [kept.Face, other.Face], recordedSize: 2, recordedMedoid: FaceId.New());

        await RepairAsync();

        var medoid = (await _clusters.GetByIdAsync(cluster, default))!.Value.MedoidFaceId;

        new[] { kept.Face, other.Face }.Should().Contain(medoid,
            "the cover must be a face the group actually holds");
    }

    /// <remarks>
    /// What makes this safe to offer as a button rather than a migration. It recomputes from the
    /// faces rather than adjusting what is there, so a second press must find nothing to do.
    /// </remarks>
    [Fact]
    public async Task Repairing_twice_changes_nothing_the_second_time()
    {
        var alice = await AddPersonAsync("Alice", cover: FaceId.New(), centroid: Unit(3));
        var face = await AddFaceAsync(alice, axis: 0);
        await AddClusterAsync([face.Face], recordedSize: 9);

        var first = await RepairAsync();
        first.FoundNothingWrong.Should().BeFalse("the library started out drifted");

        var second = await RepairAsync();

        second.FoundNothingWrong.Should().BeTrue();
        second.PeopleCalibrated.Should().Be(1, "everybody is still examined, just not corrected");
    }

    [Fact]
    public async Task A_library_that_is_already_correct_is_left_alone()
    {
        var alice = await AddPersonAsync("Alice");
        var face = await AddFaceAsync(alice);
        await AddClusterAsync([face.Face], recordedSize: 1);

        await RepairAsync();
        var second = await RepairAsync();

        second.FoundNothingWrong.Should().BeTrue();
    }

    public void Dispose() => _database.Dispose();
}

/// <summary>A progress sink for tests that are not about progress.</summary>
internal sealed class NullProgress : IProgressSink
{
    public static readonly NullProgress Instance = new();

    public void Report(ProgressUpdate update)
    {
    }
}
