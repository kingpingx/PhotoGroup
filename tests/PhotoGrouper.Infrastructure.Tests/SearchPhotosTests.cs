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
/// Covers finding photographs by who is in them.
/// </summary>
/// <remarks>
/// The question the whole application exists to answer, and the first screen where the work of
/// scanning, detecting, grouping and naming pays for itself.
///
/// The case worth the most attention is more than one person at once, because it is the only thing
/// here that a person's own page cannot already do, and because the two readings of it are
/// genuinely different: everybody named, which is what somebody means by a photograph of the two of
/// them, against anybody named, which is what they mean by everything from the holiday. Confusing
/// them produces an answer that looks plausible and is wrong.
/// </remarks>
public sealed class SearchPhotosTests : IDisposable
{
    private const string Detector = "test.detector";

    private readonly TemporaryDatabase _database = new();
    private readonly SqlitePhotoRepository _photos;
    private readonly SqliteFaceRepository _faces;
    private readonly SqlitePersonRepository _people;
    private readonly SearchPhotosUseCase _subject;

    public SearchPhotosTests()
    {
        _photos = new SqlitePhotoRepository(_database.Connections);
        _faces = new SqliteFaceRepository(_database.Connections);
        _people = new SqlitePersonRepository(_database.Connections);

        _subject = new SearchPhotosUseCase(_faces, _people, _photos);
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

    /// <summary>A photograph holding one face per person named.</summary>
    private async Task<PhotoId> AddPhotoAsync(
        string fileName, params PersonId[] appearing)
    {
        var photo = new Photo(
            PhotoId.New(), $@"D:\photos\{fileName}", 1000, DateTimeOffset.UnixEpoch);
        await _photos.UpsertAsync(photo, default);

        var faces = appearing
            .Select(_ => new Face(
                FaceId.New(), photo.Id, Detector, "1",
                new FaceBox(0, 0, 100, 100, 0.9f), Landmarks))
            .ToList();

        if (faces.Count > 0)
        {
            await _faces.BulkInsertAsync(faces, default);
            await _faces.AssignAsync(
                [.. faces.Zip(appearing, (face, person) =>
                    new FaceAssignment(face.Id, person, Assignment.Auto))],
                default);
        }

        return photo.Id;
    }

    private Task<SearchResults> SearchAsync(
        IReadOnlyList<PersonId> who, bool matchAll = true, string? text = null) =>
        _subject.ExecuteAsync(new SearchQuery(who, matchAll, text), Detector, default);

    private static string[] Names(SearchResults results) =>
        [.. results.Hits.Select(h => Path.GetFileName(h.Photo.Path)).OrderBy(n => n)];

    [Fact]
    public async Task Every_photograph_of_one_person_is_found()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("a.jpg", alice);
        await AddPhotoAsync("b.jpg", alice);
        await AddPhotoAsync("c.jpg", bob);

        Names(await SearchAsync([alice])).Should().Equal("a.jpg", "b.jpg");
    }

    /// <remarks>
    /// The only question this screen can answer that a person's own page cannot.
    /// </remarks>
    [Fact]
    public async Task Asking_for_everybody_finds_only_photographs_holding_them_all()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("together.jpg", alice, bob);
        await AddPhotoAsync("alice-alone.jpg", alice);
        await AddPhotoAsync("bob-alone.jpg", bob);

        Names(await SearchAsync([alice, bob], matchAll: true)).Should().Equal("together.jpg");
    }

    [Fact]
    public async Task Asking_for_anybody_finds_photographs_holding_either()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("together.jpg", alice, bob);
        await AddPhotoAsync("alice-alone.jpg", alice);
        await AddPhotoAsync("bob-alone.jpg", bob);
        await AddPhotoAsync("nobody.jpg");

        Names(await SearchAsync([alice, bob], matchAll: false))
            .Should().Equal("alice-alone.jpg", "bob-alone.jpg", "together.jpg");
    }

    /// <remarks>
    /// A result has to explain itself: a grid of photographs with no indication of why each one
    /// matched is indistinguishable from a grid of the whole library.
    /// </remarks>
    [Fact]
    public async Task A_result_names_everybody_in_it()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");

        await AddPhotoAsync("together.jpg", alice, bob);

        var hit = (await SearchAsync([alice])).Hits.Should().ContainSingle().Subject;

        // Passed as an array so the reason is a reason, not another expected name.
        hit.PeopleInPhoto.Should().Equal(new[] { "Alice", "Bob" },
            "the other person in the photograph is worth knowing about");
    }

    [Fact]
    public async Task Text_narrows_a_search_by_person()
    {
        var alice = await AddPersonAsync("Alice");

        await AddPhotoAsync("holiday-01.jpg", alice);
        await AddPhotoAsync("holiday-02.jpg", alice);
        await AddPhotoAsync("wedding-01.jpg", alice);

        Names(await SearchAsync([alice], text: "holiday"))
            .Should().Equal("holiday-01.jpg", "holiday-02.jpg");
    }

    /// <remarks>
    /// How somebody looks for a photograph they remember by when it was taken rather than by who is
    /// in it, and the only path that reaches photographs holding nobody at all.
    /// </remarks>
    [Fact]
    public async Task Text_alone_searches_the_whole_library()
    {
        await AddPhotoAsync("IMG_2024_holiday.jpg");
        await AddPhotoAsync("IMG_2023_wedding.jpg");

        Names(await SearchAsync([], text: "2024")).Should().Equal("IMG_2024_holiday.jpg");
    }

    /// <remarks>
    /// Underscores and percent signs are ordinary in camera file names and are both wildcards in
    /// the query this becomes. Unescaped, an underscore matches any single character, so a search
    /// for one file quietly returns its neighbours.
    /// </remarks>
    [Fact]
    public async Task Text_holding_a_wildcard_character_is_taken_literally()
    {
        await AddPhotoAsync("IMG_01.jpg");
        await AddPhotoAsync("IMGX01.jpg");

        Names(await SearchAsync([], text: "IMG_01"))
            .Should().Equal(new[] { "IMG_01.jpg" }, "an underscore is a character, not a wildcard");
    }

    /// <remarks>
    /// Returning the whole library would not be an answer to a question nobody asked, and it is the
    /// state this screen opens in.
    /// </remarks>
    [Fact]
    public async Task Asking_nothing_returns_nothing()
    {
        var alice = await AddPersonAsync("Alice");
        await AddPhotoAsync("a.jpg", alice);

        var results = await SearchAsync([]);

        results.Hits.Should().BeEmpty();
        results.TotalMatched.Should().Be(0);
    }

    [Fact]
    public async Task A_person_who_appears_in_nothing_finds_nothing()
    {
        var alice = await AddPersonAsync("Alice");
        var bob = await AddPersonAsync("Bob");
        await AddPhotoAsync("a.jpg", alice);

        (await SearchAsync([bob])).Hits.Should().BeEmpty();
    }

    /// <remarks>
    /// The limit exists because the grid is scanned rather than paged, but a search that silently
    /// shows less than it found reads as a search that missed things.
    /// </remarks>
    [Fact]
    public async Task More_matches_than_one_screen_holds_are_reported_rather_than_hidden()
    {
        var alice = await AddPersonAsync("Alice");

        for (var i = 0; i < SearchPhotosUseCase.MaximumResults + 20; i++)
        {
            await AddPhotoAsync($"{i:D4}.jpg", alice);
        }

        var results = await SearchAsync([alice]);

        results.Hits.Should().HaveCount(SearchPhotosUseCase.MaximumResults);
        results.Truncated.Should().BeTrue();
        results.TotalMatched.Should().Be(SearchPhotosUseCase.MaximumResults + 20);
    }

    public void Dispose() => _database.Dispose();
}
