using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Application.Ports;

/// <summary>Storage for named people.</summary>
public interface IPersonRepository
{
    Task<IReadOnlyList<Person>> GetAllAsync(CancellationToken ct);

    Task<Person?> GetByIdAsync(PersonId id, CancellationToken ct);

    Task<Person?> GetByNameAsync(PersonName name, CancellationToken ct);

    Task AddAsync(Person person, CancellationToken ct);

    Task UpdateAsync(Person person, CancellationToken ct);

    /// <summary>Removes the person; their faces are detached rather than deleted.</summary>
    Task RemoveAsync(PersonId id, CancellationToken ct);
}
