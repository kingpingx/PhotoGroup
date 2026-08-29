using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Application.UseCases;

/// <summary>
/// Gives every group still waiting a numbered placeholder name.
/// </summary>
/// <remarks>
/// Naming is the one thing in this application no algorithm can do, and it is also what a user
/// must do dozens of times before the library answers a single question. A placeholder is not a
/// real name, but it converts an undifferentiated wall of groups into people who can be opened,
/// merged, corrected and renamed properly later — and it makes the ones worth a real name obvious,
/// because they are the ones with the most photographs.
///
/// Built on top of naming rather than beside it. Naming a group also claims the other groups that
/// look like the same person, and reproducing that here would mean a second path that has to
/// remember to update centroids and respect the user's own corrections.
/// </remarks>
public sealed class AutoNameGroupsUseCase(
    IPersonRepository people,
    IClusterRepository clusters,
    NamePersonUseCase naming)
{
    /// <summary>Names every unnamed group as the prefix followed by a number.</summary>
    /// <param name="prefix">The stem, such as "Person". Numbers are appended to it.</param>
    public async Task<AutoNamingResult> ExecuteAsync(
        string prefix, string detectorId, string embedderId, CancellationToken ct)
    {
        var stem = (prefix ?? string.Empty).Trim();
        if (stem.Length == 0)
        {
            return AutoNamingResult.Invalid("Give a name to number, such as Person.");
        }

        // Validated once, on the first name this would produce, rather than being discovered part
        // way through a run that has already created people.
        if (!PersonName.TryCreate($"{stem} 1", out _, out var error))
        {
            return AutoNamingResult.Invalid(error!);
        }

        var taken = (await people.GetAllAsync(ct).ConfigureAwait(false))
            .Select(person => person.Name.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Largest first, so the numbers land in the order somebody would have named them by hand
        // and the low numbers belong to the people who actually matter in this library.
        var pending = (await clusters.GetAllAsync(detectorId, embedderId, ct).ConfigureAwait(false))
            .Where(cluster => cluster.PersonId is null)
            .OrderByDescending(cluster => cluster.Size)
            .ThenBy(cluster => cluster.Id.Value)
            .ToList();

        var named = 0;
        var skipped = 0;
        var next = 1;

        foreach (var cluster in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Re-read rather than trusting the list. Naming one group claims every other group
            // that looks like the same person, so a group listed a moment ago may already belong
            // to somebody; naming it again would split one person across two placeholders.
            var current = await clusters.GetByIdAsync(cluster.Id, ct).ConfigureAwait(false);
            if (current is not { PersonId: null })
            {
                continue;
            }

            string candidate;
            do
            {
                candidate = $"{stem} {next++}";
            }
            while (taken.Contains(candidate));

            var result = await naming.ExecuteAsync(cluster.Id, candidate, ct).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                taken.Add(candidate);
                named++;
            }
            else
            {
                // A group whose faces have all been placed by hand cannot be named, and that is a
                // correct refusal rather than a failure of this operation. The number is not
                // consumed, so the next group gets it instead of leaving a gap.
                next--;
                skipped++;
            }
        }

        return AutoNamingResult.Succeeded(named, skipped);
    }
}

/// <param name="Named">Groups that were given a placeholder.</param>
/// <param name="Skipped">Groups naming refused, left exactly as they were.</param>
public readonly record struct AutoNamingResult(bool IsSuccess, int Named, int Skipped, string? Error)
{
    public static AutoNamingResult Succeeded(int named, int skipped) => new(true, named, skipped, null);

    public static AutoNamingResult Invalid(string error) => new(false, 0, 0, error);
}
