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
/// Covers noticing that two names belong to one person.
/// </summary>
/// <remarks>
/// Nothing else in the application compares two named people. Naming compares a group against the
/// named, and clustering compares faces against faces; the gap between them is exactly where
/// somebody gets named twice, and it widens every time a library grows.
///
/// Nothing here changes anything, so the tests are about what is offered rather than what is done:
/// that two strangers are never put in the same set, that a chain of near matches comes out as one
/// set rather than several, and that the name suggested for keeping is the established one.
/// </remarks>
public sealed class FindDuplicatePeopleTests : IDisposable
{
    private const string Detector = "test.detector";
    private const int Dimensions = 32;

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly FindDuplicatePeopleUseCase _subject;

    public FindDuplicatePeopleTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);
        _subject = new FindDuplicatePeopleUseCase(_people, _faces);
    }

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    /// <summary>
    /// A unit vector at a chosen angle from the first axis.
    /// </summary>
    /// <remarks>
    /// Written as an angle so a test can ask for a similarity directly: two vectors this far apart
    /// have a cosine of exactly the cosine of the angle between them. Building faces and embedding
    /// them to reach a chosen similarity would couple these tests to a model they are not about.
    /// </remarks>
    private static float[] AtAngle(double degrees)
    {
        var v = new float[Dimensions];
        v[0] = (float)Math.Cos(degrees * Math.PI / 180);
        v[1] = (float)Math.Sin(degrees * Math.PI / 180);
        return v;
    }

    private async Task<PersonId> AddAsync(string name, double degrees, int photoCount = 1)
    {
        var person = new Person(
            PersonId.New(), PersonName.Create(name), DateTimeOffset.UnixEpoch,
            centroid: AtAngle(degrees));

        await _people.AddAsync(person, default);

        var faces = new List<Face>();
        for (var i = 0; i < photoCount; i++)
        {
            var photo = new Photo(
                PhotoId.New(), $@"D:\photos\{Guid.NewGuid():N}.jpg", 1000, DateTimeOffset.UnixEpoch);
            await _photos.UpsertAsync(photo, default);

            faces.Add(new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks));
        }

        await _faces.BulkInsertAsync(faces, default);
        await _faces.AssignAsync(
            [.. faces.Select(f => new FaceAssignment(f.Id, person.Id, Assignment.Auto))], default);

        return person.Id;
    }

    private Task<IReadOnlyList<DuplicatePersonGroup>> FindAsync(float threshold = 0.5f) =>
        _subject.ExecuteAsync(Detector, threshold, default);

    [Fact]
    public async Task Two_names_with_the_same_face_are_offered_as_one_person()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Alice 2", 5);

        var groups = await FindAsync();

        groups.Should().HaveCount(1);
        groups[0].Members.Should().HaveCount(2);
    }

    /// <remarks>
    /// The failure that matters most here. Offering to merge two different people invites somebody
    /// to destroy a name and file one person's photographs under another's, which no later
    /// correction can fully undo.
    /// </remarks>
    [Fact]
    public async Task Two_different_people_are_not_offered()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Bob", 80);

        (await FindAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_named_only_once_is_not_a_set_of_one()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Alice 2", 5);
        await AddAsync("Bob", 80);

        var groups = await FindAsync();

        groups.Should().HaveCount(1);
        groups[0].Members.Should().OnlyContain(m => m.Name.StartsWith("Alice"));
    }

    /// <remarks>
    /// Three names for one person where only consecutive pairs clear the bar must come out as one
    /// set. Reporting overlapping pairs would ask the user the same question twice and let them
    /// merge into two survivors who are still each other's duplicate.
    /// </remarks>
    [Fact]
    public async Task A_chain_of_near_matches_forms_a_single_set()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Alice 2", 40);
        await AddAsync("Alice 3", 80);

        var groups = await FindAsync();

        groups.Should().HaveCount(1);
        groups[0].Members.Should().HaveCount(3);
    }

    /// <remarks>
    /// The name with the most photographs is the one most likely to have been given deliberately
    /// rather than typed to clear a tile off the screen, and keeping it moves the fewest faces.
    /// </remarks>
    [Fact]
    public async Task The_name_with_the_most_photographs_is_suggested_for_keeping()
    {
        await AddAsync("1", 0, photoCount: 1);
        await AddAsync("Grandad", 4, photoCount: 9);

        var groups = await FindAsync();

        groups[0].Best.Name.Should().Be("Grandad");
        groups[0].CombinedPhotoCount.Should().Be(10);
    }

    /// <remarks>
    /// Somebody named before their photographs were embedded has no average to compare. Guessing
    /// from the name would match every pair of people a user numbered rather than named.
    /// </remarks>
    [Fact]
    public async Task Somebody_with_no_average_face_is_left_out()
    {
        await _people.AddAsync(
            new Person(PersonId.New(), PersonName.Create("Unembedded"), DateTimeOffset.UnixEpoch), default);

        await AddAsync("Alice", 0);

        (await FindAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_name_reports_how_alike_it_is()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Alice 2", 30);

        var groups = await FindAsync();

        // Two unit vectors thirty degrees apart have a cosine of about 0.87.
        groups[0].Members.Should().OnlyContain(m => m.Similarity > 0.8f && m.Similarity < 0.95f);
    }

    [Fact]
    public async Task The_biggest_set_is_offered_first()
    {
        await AddAsync("Alice", 0);
        await AddAsync("Alice 2", 5);

        await AddAsync("Bob", 90);
        await AddAsync("Bob 2", 93);
        await AddAsync("Bob 3", 96);

        var groups = await FindAsync();

        groups.Should().HaveCount(2);
        groups[0].Members.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_library_with_one_person_finds_nothing()
    {
        await AddAsync("Alice", 0);

        (await FindAsync()).Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();
}
