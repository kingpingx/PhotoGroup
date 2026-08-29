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
/// Covers finding faces of one person that are the same moment.
/// </summary>
/// <remarks>
/// This decides which of a person's photographs to propose taking away from them, so the number
/// that matters is not either threshold but the gap between two bands: the same person photographed
/// twice, and the same instant captured twice. Set the bar inside the first band and it offers to
/// strip somebody down to a single picture, which is the opposite of what a person is for.
///
/// Every face here already belongs to one person, so "is this the same person" is not the question
/// and the ordinary same-person bar would match everything. Vectors are written directly at chosen
/// angles rather than derived from images: what a given photograph embeds to belongs to the tests
/// of the embedder, and building images to reach a chosen similarity would couple these to a model
/// they are not about.
/// </remarks>
public sealed class FindDuplicateFacesTests : IDisposable
{
    private const string Detector = "test.detector";
    private const string Embedder = "test.embedder";
    private const int Dimensions = 32;

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SqliteEmbeddingRepository _embeddings;
    private readonly SqlitePhotoSignatureRepository _signatures;
    private readonly FindDuplicateFacesUseCase _subject;

    private PersonId _person;

    public FindDuplicateFacesTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _embeddings = new SqliteEmbeddingRepository(_database.Connections);
        _signatures = new SqlitePhotoSignatureRepository(_database.Connections);

        _subject = new FindDuplicateFacesUseCase(_faces, _embeddings, _signatures);
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    /// <summary>
    /// A unit vector at a chosen angle from the first axis.
    /// </summary>
    /// <remarks>
    /// Written as an angle so a test can ask for a similarity directly: two unit vectors this far
    /// apart have a cosine of exactly the cosine of the angle between them. Ten degrees is 0.985,
    /// well inside "the same moment"; forty-five is 0.707, squarely in the documented band for the
    /// same person in two different photographs.
    /// </remarks>
    private static float[] AtAngle(double degrees)
    {
        var v = new float[Dimensions];
        v[0] = (float)Math.Cos(degrees * Math.PI / 180);
        v[1] = (float)Math.Sin(degrees * Math.PI / 180);
        return v;
    }

    private async Task<PersonId> EnsurePersonAsync()
    {
        if (_person != default)
        {
            return _person;
        }

        var person = new Person(PersonId.New(), PersonName.Create("Alice"), DateTimeOffset.UnixEpoch);
        await _people.AddAsync(person, default);
        _person = person.Id;
        return _person;
    }

    /// <summary>Gives the person one face, with a vector at the given angle.</summary>
    private async Task<FaceId> AddFaceAsync(
        double degrees,
        int facePixels = 100,
        bool confirmed = false,
        double? photoSharpness = null,
        bool embedded = true,
        PersonId? owner = null)
    {
        var personId = owner ?? await EnsurePersonAsync();

        var photo = new Photo(
            PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var face = new Face(
            FaceId.New(), photo.Id, Detector, "1",
            new FaceBox(0, 0, facePixels, facePixels, 0.9f), Landmarks);

        await _faces.BulkInsertAsync([face], default);

        if (embedded)
        {
            await _embeddings.BulkUpsertAsync(
                Embedder, "1", [new FaceEmbedding(face.Id, AtAngle(degrees))], default);
        }

        if (photoSharpness is { } sharpness)
        {
            await _signatures.BulkUpsertAsync(
                [new PhotoSignature(photo.Id, new PerceptualHash(0, 0), sharpness)], default);
        }

        await _faces.AssignAsync(
            [new FaceAssignment(face.Id, personId, confirmed ? Assignment.Confirmed : Assignment.Auto)],
            default);

        return face.Id;
    }

    private async Task<IReadOnlyList<DuplicateFaceSet>> FindAsync(float threshold = 0.92f) =>
        await _subject.ExecuteAsync(await EnsurePersonAsync(), Detector, Embedder, threshold, default);

    [Fact]
    public async Task Two_frames_of_one_moment_are_offered_as_a_set()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(5);

        var sets = await FindAsync();

        sets.Should().HaveCount(1);
        sets[0].Members.Should().HaveCount(2);
        sets[0].ExtraCount.Should().Be(1);
    }

    /// <remarks>
    /// The central test. Everything compared here is already one person, so the bar has to sit
    /// above the band the same person occupies across different photographs — measured on this
    /// project's reference photographs at 0.62 to 0.81. Forty-five degrees is a cosine of 0.707,
    /// the middle of it. A bar that caught this would offer to delete most of somebody's library.
    /// </remarks>
    [Fact]
    public async Task Two_ordinary_photographs_of_the_same_person_are_not()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(45);

        (await FindAsync()).Should().BeEmpty(
            "0.707 is the same person in two pictures, not the same picture twice");
    }

    [Fact]
    public async Task A_face_matching_nothing_is_not_a_set_of_one()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(5);
        await AddFaceAsync(60);

        var sets = await FindAsync();

        sets.Should().HaveCount(1);
        sets[0].Members.Should().HaveCount(2);
    }

    /// <remarks>
    /// Frame one matches frame two and frame two frame three, without one and three clearing the
    /// bar against each other. A burst is one set; reporting overlapping pairs would ask the user
    /// the same question twice and let them keep two frames of the same instant.
    /// </remarks>
    [Fact]
    public async Task Three_frames_where_only_consecutive_pairs_match_form_one_set()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(20);
        await AddFaceAsync(40);

        var sets = await FindAsync(0.92f);

        sets.Should().HaveCount(1, "each frame matches its neighbour, so the burst is one set");
        sets[0].Members.Should().HaveCount(3);
    }

    /// <remarks>
    /// The user looked at that face and said it was this person. No measurement earns the right to
    /// propose removing it in favour of one the application merely guessed — which is the same
    /// principle that stops naming touching a face somebody has decided about.
    /// </remarks>
    [Fact]
    public async Task A_face_the_user_confirmed_is_the_one_kept()
    {
        var confirmed = await AddFaceAsync(0, facePixels: 60, confirmed: true);
        await AddFaceAsync(5, facePixels: 400);

        var sets = await FindAsync();

        sets[0].Keeper.FaceId.Should().Be(confirmed, "a confirmed face outranks a larger guess");
    }

    [Fact]
    public async Task The_largest_face_is_kept_when_nothing_was_confirmed()
    {
        await AddFaceAsync(0, facePixels: 80);
        var largest = await AddFaceAsync(5, facePixels: 400);

        (await FindAsync())[0].Keeper.FaceId.Should().Be(largest);
    }

    /// <remarks>
    /// The same number the duplicate-photo tool uses to choose between burst frames, which is
    /// reliably present for anyone who has run it.
    /// </remarks>
    [Fact]
    public async Task Photo_sharpness_settles_a_tie_between_faces_of_equal_size()
    {
        await AddFaceAsync(0, facePixels: 200, photoSharpness: 10);
        var sharpest = await AddFaceAsync(5, facePixels: 200, photoSharpness: 900);

        (await FindAsync())[0].Keeper.FaceId.Should().Be(sharpest);
    }

    /// <remarks>
    /// A face with no vector has not been judged, so putting it in a set would present a guess as a
    /// measurement. Treating it as a zero vector would be harmless arithmetically and dishonest.
    /// </remarks>
    [Fact]
    public async Task Faces_without_a_vector_are_ignored_rather_than_matching_everything()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(5);
        var unembedded = await AddFaceAsync(0, embedded: false);

        var sets = await FindAsync();

        sets.Should().HaveCount(1);
        sets[0].Members.Should().NotContain(m => m.FaceId == unembedded);
    }

    [Fact]
    public async Task Another_persons_faces_are_never_considered()
    {
        var stranger = new Person(PersonId.New(), PersonName.Create("Bob"), DateTimeOffset.UnixEpoch);
        await _people.AddAsync(stranger, default);

        await AddFaceAsync(0);
        await AddFaceAsync(0, owner: stranger.Id);

        (await FindAsync()).Should().BeEmpty("only one face belongs to the person being examined");
    }

    [Fact]
    public async Task A_person_with_a_single_face_has_nothing_to_offer()
    {
        await AddFaceAsync(0);

        (await FindAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_biggest_set_is_offered_first()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(3);

        await AddFaceAsync(90);
        await AddFaceAsync(93);
        await AddFaceAsync(96);

        var sets = await FindAsync();

        sets.Should().HaveCount(2);
        sets[0].Members.Should().HaveCount(3);
    }

    [Fact]
    public async Task Each_face_reports_how_alike_it_is()
    {
        await AddFaceAsync(0);
        await AddFaceAsync(10);

        // Two unit vectors ten degrees apart have a cosine of about 0.985.
        (await FindAsync())[0].Members.Should()
            .OnlyContain(m => m.Similarity > 0.97f && m.Similarity <= 1f);
    }

    public void Dispose() => _database.Dispose();
}
