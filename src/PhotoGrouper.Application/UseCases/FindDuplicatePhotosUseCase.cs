using PhotoGrouper.Application.Clustering;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Finds sets of photographs that are the same picture.
/// </summary>
/// <remarks>
/// Aimed at the burst: eight frames of one scene taken in as many seconds, differing in a blink
/// and a shift of the shoulders, of which a person wants one. That is a harder question than
/// finding identical files, because no two of those frames share a byte, and it is a more
/// dangerous one, because the answer is used to move originals off somebody's disk.
///
/// Two things guard against a wrong answer. The fingerprints must be close, and the photographs
/// must have been taken close together in time where both carry a capture time. The second
/// condition is what separates a burst from a coincidence: two photographs of the same wall a year
/// apart are not a burst, and a tool that swept one of them away would be worse than useless.
/// </remarks>
public sealed class FindDuplicatePhotosUseCase(
    IPhotoSignatureRepository signatures,
    IPhotoReader photos)
{
    /// <summary>
    /// How many of the hundred and twenty-eight bits may differ before two photographs are
    /// different pictures.
    /// </summary>
    /// <remarks>
    /// Identical pictures score zero. Frames from one burst land in the low figures: the
    /// fingerprint describes where the image gets lighter and darker, and a blink moves very few of
    /// those boundaries. Unrelated photographs are typically past forty.
    ///
    /// Twelve sits well inside that gap rather than at the edge of it, and is deliberately nearer
    /// the cautious end, because the two mistakes are not equal: missing a duplicate leaves a user
    /// with an extra photograph, and inventing one offers to move away a picture they wanted.
    /// </remarks>
    public const int DefaultMaximumDistance = 12;

    /// <summary>
    /// How far apart in capture time two frames of one burst may be.
    /// </summary>
    /// <remarks>
    /// Generous, because a burst is not always the camera's own: somebody taking the same shot
    /// three times to get one where nobody blinked takes half a minute over it. Applied only when
    /// both photographs carry a capture time — a scan, a screenshot or a file stripped of its EXIF
    /// has none, and refusing to consider those would silently exclude them.
    /// </remarks>
    public static readonly TimeSpan DefaultMaximumApart = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<DuplicateGroup>> ExecuteAsync(
        int maximumDistance,
        TimeSpan? maximumApart,
        CancellationToken ct)
    {
        var all = await signatures.GetAllAsync(ct).ConfigureAwait(false);
        if (all.Count < 2)
        {
            return [];
        }

        var details = new Dictionary<PhotoId, Photo>(all.Count);
        await foreach (var photo in photos.StreamAllAsync(ct).ConfigureAwait(false))
        {
            details[photo.Id] = photo;
        }

        // Only photographs still in the index are considered. A fingerprint can outlive its photo
        // for as long as it takes a cascade to run, and a group naming a photograph nothing can
        // open is not something to put in front of somebody about to delete files.
        var present = all.Where(s => details.ContainsKey(s.PhotoId)).ToList();

        var union = new UnionFind(present.Count);

        for (var i = 0; i < present.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            for (var j = i + 1; j < present.Count; j++)
            {
                if (present[i].Hash.DistanceTo(present[j].Hash) > maximumDistance)
                {
                    continue;
                }

                if (!TakenCloseTogether(
                        details[present[i].PhotoId], details[present[j].PhotoId], maximumApart))
                {
                    continue;
                }

                union.Join(i, j);
            }
        }

        var groups = new List<DuplicateGroup>();

        foreach (var indices in union.Sets())
        {
            var group = indices.Select(i => present[i]).ToList();

            // Ordered so the first is the one worth keeping. Sharpest first, because between two
            // frames of one scene that is the only difference that matters; then the largest, which
            // is the least re-compressed; then the earliest, which for a burst is the frame taken
            // before anybody reacted to the shutter.
            var ordered = group
                .OrderByDescending(s => s.Sharpness)
                .ThenByDescending(s => details[s.PhotoId].FileSize)
                .ThenBy(s => details[s.PhotoId].TakenUtc ?? details[s.PhotoId].ModifiedUtc)
                .Select(s => new DuplicateMember(
                    details[s.PhotoId],
                    s.Sharpness,
                    s.Hash.DistanceTo(group.MaxBy(m => m.Sharpness).Hash)))
                .ToList();

            groups.Add(new DuplicateGroup(ordered));
        }

        // Largest sets first: a burst of eight is where the space is, and where a decision covers
        // the most files for the same moment of attention.
        return [.. groups
            .OrderByDescending(g => g.Members.Count)
            .ThenByDescending(g => g.RecoverableBytes)];
    }

    /// <remarks>
    /// True when either photograph has no capture time. Refusing to pair those would exclude every
    /// screenshot and every file whose EXIF a messaging app stripped, which is a large part of the
    /// duplicates a real library contains.
    /// </remarks>
    private static bool TakenCloseTogether(Photo a, Photo b, TimeSpan? maximumApart)
    {
        if (maximumApart is not { } window)
        {
            return true;
        }

        if (a.TakenUtc is not { } first || b.TakenUtc is not { } second)
        {
            return true;
        }

        return (first - second).Duration() <= window;
    }

}

/// <summary>A set of photographs that are the same picture, best first.</summary>
public sealed record DuplicateGroup(IReadOnlyList<DuplicateMember> Members)
{
    /// <summary>The one suggested for keeping.</summary>
    public DuplicateMember Best => Members[0];

    /// <summary>Bytes that would come back if everything but the best were removed.</summary>
    public long RecoverableBytes => Members.Skip(1).Sum(m => m.Photo.FileSize);
}

/// <param name="Sharpness">Fine detail carried, comparable only against the others in this group.</param>
/// <param name="DistanceFromBest">How many fingerprint bits differ from the suggested keeper.</param>
public readonly record struct DuplicateMember(Photo Photo, double Sharpness, int DistanceFromBest);
