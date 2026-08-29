using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Answers the question the whole application exists for: show me every photograph of somebody.
/// </summary>
/// <remarks>
/// Everything before this stage finds and measures faces; naming turns those measurements into
/// people. This is the first screen where that work pays for itself, and it is the only place that
/// can answer a question about more than one person at once — "the two of them together" is not a
/// question a person's own page can be asked.
///
/// Photographs are found through the faces rather than through any stored per-photo list, so an
/// answer can never disagree with what the People screen shows. The cost is a query per person
/// asked about, which is a handful; the alternative is a denormalised table that has to be kept
/// correct through every merge, move and rejection, and would be wrong exactly when somebody has
/// just corrected something.
/// </remarks>
public sealed class SearchPhotosUseCase(
    IFaceRepository faces,
    IPersonRepository people,
    IPhotoReader photos)
{
    /// <summary>
    /// How many photographs one search will return.
    /// </summary>
    /// <remarks>
    /// A bound rather than paging, because the screen this feeds is a grid somebody scans rather
    /// than a report they page through, and a search matching four thousand photographs is a search
    /// that needs narrowing rather than showing. The count of what was left out is reported, so the
    /// limit is never silent.
    /// </remarks>
    public const int MaximumResults = 500;

    public async Task<SearchResults> ExecuteAsync(
        SearchQuery query, string detectorId, CancellationToken ct)
    {
        var named = await people.GetAllAsync(ct).ConfigureAwait(false);
        var namesById = named.ToDictionary(person => person.Id, person => person.Name.Value);

        var matched = query.People.Count > 0
            ? await PhotosOfAsync(query, detectorId, ct).ConfigureAwait(false)
            : null;

        // With nobody chosen the text alone drives the search, which is what somebody typing a
        // camera's file naming into the box means. With nobody chosen and no text there is no
        // question to answer, and returning the whole library would not be an answer to it.
        if (matched is null && string.IsNullOrWhiteSpace(query.FileNameContains))
        {
            return new SearchResults([], 0, false);
        }

        var candidates = matched is null
            ? await photos
                .SearchByPathAsync(query.FileNameContains!.Trim(), MaximumResults + 1, ct)
                .ConfigureAwait(false)
            : await LoadAsync(matched, ct).ConfigureAwait(false);

        var filtered = string.IsNullOrWhiteSpace(query.FileNameContains) || matched is null
            ? candidates
            : [.. candidates.Where(photo => Contains(photo.Path, query.FileNameContains))];

        var ordered = filtered
            .OrderByDescending(photo => photo.TakenUtc ?? photo.ModifiedUtc)
            .ThenBy(photo => photo.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var truncated = ordered.Count > MaximumResults;
        var shown = truncated ? ordered.Take(MaximumResults).ToList() : ordered;

        var hits = new List<SearchHit>(shown.Count);

        foreach (var photo in shown)
        {
            ct.ThrowIfCancellationRequested();

            var inPhoto = await faces.GetByPhotoAsync(photo.Id, detectorId, ct).ConfigureAwait(false);

            // Filtered to the active detector's living faces. That query does not apply it, unlike
            // the one that fetches a person's faces, so a previous detector's rows would otherwise
            // list people twice.
            hits.Add(new SearchHit(
                photo,
                [.. inPhoto
                    .Where(face => face.IsActive && face.PersonId is { } id && namesById.ContainsKey(id))
                    .Select(face => namesById[face.PersonId!.Value])
                    .Distinct()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]));
        }

        return new SearchResults(hits, ordered.Count, truncated);
    }

    /// <summary>
    /// The photographs holding the people asked about.
    /// </summary>
    /// <remarks>
    /// Everyone, or anyone, and the difference is the point of asking about more than one person.
    /// "Everyone" is what somebody means by a photograph of the two of them; "anyone" is what they
    /// mean by everything from the holiday.
    /// </remarks>
    private async Task<HashSet<PhotoId>> PhotosOfAsync(
        SearchQuery query, string detectorId, CancellationToken ct)
    {
        HashSet<PhotoId>? accumulated = null;

        foreach (var personId in query.People)
        {
            ct.ThrowIfCancellationRequested();

            var theirs = (await faces.GetByPersonAsync(personId, detectorId, ct).ConfigureAwait(false))
                .Select(face => face.PhotoId)
                .ToHashSet();

            if (accumulated is null)
            {
                accumulated = theirs;
            }
            else if (query.MatchAll)
            {
                accumulated.IntersectWith(theirs);
            }
            else
            {
                accumulated.UnionWith(theirs);
            }

            // Nothing can come back once an intersection is empty, and a library where somebody
            // asked for four people together will usually empty on the second.
            if (query.MatchAll && accumulated.Count == 0)
            {
                break;
            }
        }

        return accumulated ?? [];
    }

    private async Task<List<Photo>> LoadAsync(HashSet<PhotoId> ids, CancellationToken ct)
    {
        var results = new List<Photo>(ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            if (await photos.GetByIdAsync(id, ct).ConfigureAwait(false) is { } photo)
            {
                results.Add(photo);
            }
        }

        return results;
    }

    private static bool Contains(string path, string? fragment) =>
        !string.IsNullOrWhiteSpace(fragment)
        && Path.GetFileName(path).Contains(fragment.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <param name="People">Who to look for. Empty means the text alone decides.</param>
/// <param name="MatchAll">
/// True for photographs holding everybody named, false for photographs holding any of them.
/// </param>
/// <param name="FileNameContains">Optional text matched against the file's name, not its folder.</param>
public sealed record SearchQuery(
    IReadOnlyList<PersonId> People, bool MatchAll, string? FileNameContains);

/// <param name="TotalMatched">Everything the query matched, including what the limit left out.</param>
/// <param name="Truncated">True when more was found than one screen will show.</param>
public sealed record SearchResults(
    IReadOnlyList<SearchHit> Hits, int TotalMatched, bool Truncated);

/// <param name="PeopleInPhoto">Everybody named who appears here, so a result explains itself.</param>
public readonly record struct SearchHit(Photo Photo, IReadOnlyList<string> PeopleInPhoto);
