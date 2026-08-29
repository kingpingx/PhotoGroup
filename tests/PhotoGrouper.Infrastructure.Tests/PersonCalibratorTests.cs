using FluentAssertions;
using PhotoGrouper.Application.People;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers bringing a person's derived data back in line with the faces they hold.
/// </summary>
/// <remarks>
/// A person carries two things nobody typed: the average of their face vectors and the face on
/// their tile. Neither announces that it has gone stale, and the average is the dangerous one — it
/// is what a later grouping run compares new faces against, so a person still averaging photographs
/// they no longer have quietly collects strangers who resemble those photographs.
///
/// This used to live in three places that had already drifted apart: one cleared the average when a
/// person was emptied, the other two left the old one in place. These tests pin the behaviour the
/// single implementation now has, including the two places where it deliberately differs from what
/// two of the three copies did.
/// </remarks>
public sealed class PersonCalibratorTests : IDisposable
{
    private const string Detector = "test.detector";
    private const string Embedder = "test.embedder";

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly PersonCalibrator _subject;

    public PersonCalibratorTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);

        _subject = new PersonCalibrator(_people, _faces, _embeddings);
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    /// <summary>A unit vector along one axis, so an average over a set is easy to read.</summary>
    private static float[] Unit(int axis)
    {
        var v = new float[8];
        v[axis] = 1f;
        return v;
    }

    private async Task<PersonId> AddPersonAsync(
        FaceId? cover = null, float[]? centroid = null, string name = "Alice")
    {
        var person = new Person(
            PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch, cover, centroid);

        await _people.AddAsync(person, default);
        return person.Id;
    }

    private async Task<FaceId> AddFaceAsync(
        PersonId? owner, int axis = 0, int facePixels = 100, float score = 0.9f, bool embedded = true)
    {
        var photo = new Photo(
            PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var face = new Face(
            FaceId.New(), photo.Id, Detector, "1",
            new FaceBox(0, 0, facePixels, facePixels, score), Landmarks, personId: owner);

        await _faces.BulkInsertAsync([face], default);

        if (embedded)
        {
            await _embeddings.BulkUpsertAsync(
                Embedder, "1", [new FaceEmbedding(face.Id, Unit(axis))], default);
        }

        return face.Id;
    }

    private Task<CalibrationResult> CalibrateAsync(PersonId person) =>
        _subject.CalibrateAsync(person, Detector, Embedder, default);

    private async Task<Person> ReadAsync(PersonId person) =>
        (await _people.GetByIdAsync(person, default))!;

    [Fact]
    public async Task A_persons_average_follows_the_faces_they_actually_hold()
    {
        var alice = await AddPersonAsync();
        await AddFaceAsync(alice, axis: 0);
        await AddFaceAsync(alice, axis: 1);

        await CalibrateAsync(alice);

        var centroid = (await ReadAsync(alice)).Centroid!;

        // Two unit vectors at right angles average to a vector at 45 degrees to both, which after
        // normalising is 0.707 on each axis.
        centroid[0].Should().BeApproximately(0.707f, 0.001f);
        centroid[1].Should().BeApproximately(0.707f, 0.001f);
    }

    [Fact]
    public async Task The_average_is_unit_length_so_it_compares_against_single_faces()
    {
        var alice = await AddPersonAsync();
        await AddFaceAsync(alice, axis: 0);
        await AddFaceAsync(alice, axis: 1);
        await AddFaceAsync(alice, axis: 2);

        await CalibrateAsync(alice);

        var centroid = (await ReadAsync(alice)).Centroid!;

        MathF.Sqrt(centroid.Sum(v => v * v)).Should().BeApproximately(1f, 0.001f,
            "an unnormalised mean would score lower simply for being an average");
    }

    /// <remarks>
    /// The behaviour change. Two of the three replaced copies returned early here and left the old
    /// average in place, which is worse than having none: recognition treats such a person as a live
    /// identity and files whoever resembles their former photographs under their name.
    /// </remarks>
    [Fact]
    public async Task A_person_left_with_no_faces_keeps_no_average()
    {
        var alice = await AddPersonAsync(centroid: Unit(4));

        var result = await CalibrateAsync(alice);

        result.HasCentroid.Should().BeFalse();
        (await ReadAsync(alice)).Centroid.Should().BeNull(
            "an average describing faces they no longer hold is worse than none at all");
    }

    [Fact]
    public async Task Faces_without_a_vector_are_skipped_rather_than_counted_as_zero()
    {
        var alice = await AddPersonAsync();
        await AddFaceAsync(alice, axis: 0);
        await AddFaceAsync(alice, embedded: false);

        await CalibrateAsync(alice);

        var centroid = (await ReadAsync(alice)).Centroid!;

        centroid[0].Should().BeApproximately(1f, 0.001f,
            "counting the unembedded face as a zero vector would drag the average toward nothing");
    }

    /// <remarks>
    /// Stability matters more here than picking the theoretical best every time. A tile whose
    /// picture changes after every correction is unsettling, and the user may well have chosen it.
    /// </remarks>
    [Fact]
    public async Task A_cover_face_they_still_own_is_left_alone()
    {
        var alice = await AddPersonAsync();
        var small = await AddFaceAsync(alice, axis: 0, facePixels: 40);
        await AddFaceAsync(alice, axis: 0, facePixels: 400);

        // Give them the small one as a cover, the way naming does with a group's medoid.
        var person = await ReadAsync(alice);
        person.SetCoverFace(small);
        await _people.UpdateAsync(person, default);

        var result = await CalibrateAsync(alice);

        result.CoverChanged.Should().BeFalse();
        (await ReadAsync(alice)).CoverFaceId.Should().Be(small);
    }

    [Fact]
    public async Task A_cover_face_the_person_no_longer_owns_is_replaced()
    {
        var alice = await AddPersonAsync(cover: FaceId.New());
        var theirs = await AddFaceAsync(alice, axis: 0);

        var result = await CalibrateAsync(alice);

        result.CoverChanged.Should().BeTrue();
        (await ReadAsync(alice)).CoverFaceId.Should().Be(theirs);
    }

    /// <remarks>
    /// The most typical face rather than the largest. A cover is meant to be recognisable, and the
    /// largest may be a blurred profile that happens to fill the frame.
    /// </remarks>
    [Fact]
    public async Task The_chosen_cover_is_the_face_nearest_the_average()
    {
        var alice = await AddPersonAsync();
        var typical = await AddFaceAsync(alice, axis: 0, facePixels: 60);
        await AddFaceAsync(alice, axis: 0, facePixels: 60);
        await AddFaceAsync(alice, axis: 5, facePixels: 400);

        await CalibrateAsync(alice);

        // Two of the three point one way, so the average leans that way and an outlier that merely
        // fills more of the frame does not win.
        var cover = (await ReadAsync(alice)).CoverFaceId;
        var nearest = new[] { typical };
        nearest.Should().Contain(c => c == cover!.Value,
            "the face closest to the average is the most typical of them");
    }

    /// <remarks>
    /// The column carries no foreign key, so a cover pointing at a face the person no longer holds
    /// would dangle indefinitely with nothing downstream noticing.
    /// </remarks>
    [Fact]
    public async Task A_person_with_no_faces_loses_their_cover()
    {
        var alice = await AddPersonAsync(cover: FaceId.New());

        await CalibrateAsync(alice);

        (await ReadAsync(alice)).CoverFaceId.Should().BeNull();
    }

    [Fact]
    public async Task A_face_belonging_to_somebody_else_never_counts()
    {
        var alice = await AddPersonAsync();
        var bob = await AddPersonAsync(name: "Bob");

        await AddFaceAsync(alice, axis: 0);
        await AddFaceAsync(bob, axis: 1);

        await CalibrateAsync(alice);

        (await ReadAsync(alice)).Centroid![0].Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public async Task Calibrating_somebody_who_has_gone_reports_nothing_rather_than_throwing()
    {
        var result = await CalibrateAsync(PersonId.New());

        result.FaceCount.Should().Be(0);
        result.HasCentroid.Should().BeFalse();
    }

    [Fact]
    public async Task Several_people_can_be_calibrated_in_one_pass()
    {
        var alice = await AddPersonAsync();
        var bob = await AddPersonAsync(name: "Bob");
        await AddFaceAsync(alice, axis: 0);
        await AddFaceAsync(bob, axis: 1);

        var results = await _subject.CalibrateAsync([alice, bob], Detector, Embedder, default);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.HasCentroid);
    }

    public void Dispose() => _database.Dispose();
}
