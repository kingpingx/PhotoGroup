using FluentAssertions;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Contracts.Tests;

/// <summary>
/// The behaviour every face store must exhibit.
/// </summary>
/// <remarks>
/// Heavier than the photo contract because faces carry the part of the library a user cannot
/// recreate. A photo row can be rebuilt by rescanning; a person's name, and which faces belong
/// to them, exist only because somebody sat down and said so.
///
/// The detector scoping rules get the most attention here. Both detectors' faces coexist so that
/// switching between them is reversible, which means every read has to filter, and a forgotten
/// filter shows up as duplicated people rather than as an error.
/// </remarks>
public abstract class FaceRepositoryContract
{
    protected const string DetectorA = "test.detector.a";
    protected const string DetectorB = "test.detector.b";

    /// <summary>Creates an empty face store, with the repositories its references require.</summary>
    protected abstract Task<(IFaceRepository Faces, IPhotoWriter Photos, IPersonRepository People)> CreateAsync();

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    private static Face NewFace(
        PhotoId photo,
        string detector = DetectorA,
        float score = 0.9f,
        PersonId? person = null,
        Assignment assignment = Assignment.Auto,
        bool active = true) =>
        new(FaceId.New(), photo, detector, "1",
            new FaceBox(10, 20, 100, 120, score), Landmarks,
            isActive: active, blurScore: null, personId: person, assignment: assignment);

    private static async Task<PhotoId> AddPhotoAsync(IPhotoWriter photos, string path = @"D:\photos\a.jpg")
    {
        var photo = new Photo(PhotoId.New(), path, 1000, DateTimeOffset.UnixEpoch);
        await photos.UpsertAsync(photo, default);
        return photo.Id;
    }

    /// <remarks>
    /// A real person rather than an invented id. A face may only point at someone who exists, and
    /// a store that allowed otherwise would let a person be deleted while faces still claimed
    /// them, which is exactly how a People page acquires an entry with no name.
    /// </remarks>
    private static async Task<PersonId> AddPersonAsync(IPersonRepository people, string name = "Alice")
    {
        var person = new Person(PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch);
        await people.AddAsync(person, default);
        return person.Id;
    }

    [Fact]
    public async Task An_empty_store_holds_no_faces()
    {
        var (faces, _, _) = await CreateAsync();

        (await faces.CountAsync(DetectorA, activeOnly: false, default)).Should().Be(0);
    }

    [Fact]
    public async Task Inserted_faces_can_be_read_back_for_their_photo()
    {
        var (faces, photos, _) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);

        await faces.BulkInsertAsync([NewFace(photo), NewFace(photo)], default);

        (await faces.GetByPhotoAsync(photo, DetectorA, default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Every_field_survives_a_round_trip()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var original = NewFace(photo, score: 0.8125f);

        await faces.BulkInsertAsync([original], default);
        var read = (await faces.GetByPhotoAsync(photo, DetectorA, default)).Single();

        read.Id.Should().Be(original.Id);
        read.PhotoId.Should().Be(photo);
        read.DetectorId.Should().Be(DetectorA);
        read.Box.Should().Be(original.Box);
        read.Assignment.Should().Be(Assignment.Auto);
        read.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Landmarks_survive_a_round_trip_exactly()
    {
        // Exactly, not approximately. These points drive the alignment transform, and a value
        // degraded in storage shifts every crop by a little, which costs accuracy invisibly.
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);

        await faces.BulkInsertAsync([NewFace(photo)], default);
        var read = (await faces.GetByPhotoAsync(photo, DetectorA, default)).Single();

        read.Landmarks.Should().Be(Landmarks);
        read.Landmarks.ToFloats().Should().Equal(Landmarks.ToFloats());
    }

    [Fact]
    public async Task Faces_from_different_detectors_coexist_for_one_photo()
    {
        // The basis of a reversible detector switch: the previous detector's faces are retained,
        // not deleted, so switching back does not mean processing the library again.
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);

        await faces.BulkInsertAsync([NewFace(photo, DetectorA), NewFace(photo, DetectorB)], default);

        (await faces.GetByPhotoAsync(photo, DetectorA, default)).Should().ContainSingle();
        (await faces.GetByPhotoAsync(photo, DetectorB, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_a_photos_faces_affects_only_the_named_detector()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        await faces.BulkInsertAsync([NewFace(photo, DetectorA), NewFace(photo, DetectorB)], default);

        await faces.DeleteByPhotoAsync(photo, DetectorA, default);

        (await faces.GetByPhotoAsync(photo, DetectorA, default)).Should().BeEmpty();
        (await faces.GetByPhotoAsync(photo, DetectorB, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Re_detecting_a_photo_replaces_its_faces_rather_than_adding_more()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        await faces.BulkInsertAsync([NewFace(photo), NewFace(photo)], default);

        await faces.DeleteByPhotoAsync(photo, DetectorA, default);
        await faces.BulkInsertAsync([NewFace(photo)], default);

        (await faces.GetByPhotoAsync(photo, DetectorA, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Switching_the_active_detector_deactivates_the_others()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        await faces.BulkInsertAsync([NewFace(photo, DetectorA), NewFace(photo, DetectorB)], default);

        await faces.SetActiveDetectorAsync(DetectorB, default);

        (await faces.CountAsync(DetectorA, activeOnly: true, default)).Should().Be(0);
        (await faces.CountAsync(DetectorB, activeOnly: true, default)).Should().Be(1);
        (await faces.CountAsync(DetectorA, activeOnly: false, default)).Should().Be(1,
            "the inactive set is retained so switching back is instant");
    }

    [Fact]
    public async Task Switching_back_restores_the_previous_detectors_faces()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        await faces.BulkInsertAsync([NewFace(photo, DetectorA), NewFace(photo, DetectorB)], default);

        await faces.SetActiveDetectorAsync(DetectorB, default);
        await faces.SetActiveDetectorAsync(DetectorA, default);

        (await faces.CountAsync(DetectorA, activeOnly: true, default)).Should().Be(1);
    }

    [Fact]
    public async Task A_person_can_be_assigned_to_a_face()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var face = NewFace(photo);
        await faces.BulkInsertAsync([face], default);
        var person = await AddPersonAsync(people);

        await faces.AssignAsync([new FaceAssignment(face.Id, person, Assignment.Confirmed)], default);

        var read = (await faces.GetByPhotoAsync(photo, DetectorA, default)).Single();
        read.PersonId.Should().Be(person);
        read.Assignment.Should().Be(Assignment.Confirmed);
    }

    [Fact]
    public async Task A_face_can_be_detached_from_its_person()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var face = NewFace(photo, person: await AddPersonAsync(people), assignment: Assignment.Auto);
        await faces.BulkInsertAsync([face], default);

        await faces.AssignAsync([new FaceAssignment(face.Id, null, Assignment.Rejected)], default);

        var read = (await faces.GetByPhotoAsync(photo, DetectorA, default)).Single();
        read.PersonId.Should().BeNull();
        read.Assignment.Should().Be(Assignment.Rejected);
    }

    [Fact]
    public async Task Faces_can_be_listed_for_a_person()
    {
        var (faces, photos, people) = await CreateAsync();
        var person = await AddPersonAsync(people);
        var first = await AddPhotoAsync(photos, @"D:\photos\1.jpg");
        var second = await AddPhotoAsync(photos, @"D:\photos\2.jpg");

        await faces.BulkInsertAsync(
        [
            NewFace(first, person: person),
            NewFace(second, person: person),
            NewFace(second),
        ], default);

        (await faces.GetByPersonAsync(person, DetectorA, default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_persons_faces_exclude_the_inactive_detectors_copies()
    {
        // Without this filter every person's photo count roughly doubles once a second detector
        // has run, and it reads as a clustering fault rather than a missing predicate.
        var (faces, photos, people) = await CreateAsync();
        var person = await AddPersonAsync(people);
        var photo = await AddPhotoAsync(photos);

        await faces.BulkInsertAsync(
        [
            NewFace(photo, DetectorA, person: person),
            NewFace(photo, DetectorB, person: person),
        ], default);

        await faces.SetActiveDetectorAsync(DetectorA, default);

        (await faces.GetByPersonAsync(person, DetectorA, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Streaming_returns_a_detectors_faces_in_a_stable_order()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        await faces.BulkInsertAsync(
            Enumerable.Range(0, 25).Select(_ => NewFace(photo)).ToList(), default);

        var first = await Collect(faces);
        var second = await Collect(faces);

        first.Should().HaveCount(25);
        second.Should().Equal(first);

        static async Task<List<FaceId>> Collect(IFaceRepository faces)
        {
            var ids = new List<FaceId>();
            await foreach (var face in faces.StreamByDetectorAsync(DetectorA, activeOnly: false, default))
            {
                ids.Add(face.Id);
            }

            return ids;
        }
    }

    [Fact]
    public async Task Inserting_an_empty_batch_is_harmless()
    {
        var (faces, _, _) = await CreateAsync();

        await faces.BulkInsertAsync([], default);

        (await faces.CountAsync(DetectorA, activeOnly: false, default)).Should().Be(0);
    }

    [Fact]
    public async Task Faces_can_be_fetched_by_a_list_of_ids()
    {
        // How each stage collects the batch it is about to work on. Scanning and filtering instead
        // would make a linear pass over the library quadratic.
        var (faces, photos, _) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var all = Enumerable.Range(0, 10).Select(_ => NewFace(photo)).ToList();
        await faces.BulkInsertAsync(all, default);

        var wanted = all.Take(3).Select(f => f.Id).ToList();

        var fetched = await faces.GetByIdsAsync(wanted, default);

        fetched.Select(f => f.Id).Should().BeEquivalentTo(wanted);
    }

    [Fact]
    public async Task Fetching_no_ids_returns_nothing()
    {
        var (faces, _, _) = await CreateAsync();

        (await faces.GetByIdsAsync([], default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Fetching_by_id_copes_with_more_ids_than_one_statement_can_carry()
    {
        // SQLite caps how many parameters a statement may hold. Without chunking this would work
        // in development and fail only once somebody's library grew past the limit.
        var (faces, photos, _) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var all = Enumerable.Range(0, 1500).Select(_ => NewFace(photo)).ToList();
        await faces.BulkInsertAsync(all, default);

        var fetched = await faces.GetByIdsAsync([.. all.Select(f => f.Id)], default);

        fetched.Should().HaveCount(1500);
    }

    [Fact]
    public async Task Unknown_ids_are_simply_absent_from_the_result()
    {
        var (faces, photos, _) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);
        var face = NewFace(photo);
        await faces.BulkInsertAsync([face], default);

        var fetched = await faces.GetByIdsAsync([face.Id, FaceId.New()], default);

        fetched.Should().ContainSingle();
    }

    [Fact]
    public async Task Bulk_insert_stores_every_face()
    {
        var (faces, photos, people) = await CreateAsync();
        var photo = await AddPhotoAsync(photos);

        await faces.BulkInsertAsync(
            Enumerable.Range(0, 500).Select(_ => NewFace(photo)).ToList(), default);

        (await faces.CountAsync(DetectorA, activeOnly: false, default)).Should().Be(500);
    }
}
