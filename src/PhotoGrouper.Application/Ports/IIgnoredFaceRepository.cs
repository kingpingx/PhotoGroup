using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Application.Ports;

/// <summary>
/// Faces the user has said they do not care about.
/// </summary>
/// <remarks>
/// Strangers in the background, faces on posters, people walking past. Without a way to dismiss
/// them they form groups that sit on the People screen indefinitely asking to be named, and no
/// answer makes them go away.
///
/// Kept per face rather than per group, because groups are rebuilt from scratch on every run and
/// carry no identity between them: a dismissal recorded against a group would be forgotten the next
/// time grouping was pressed.
/// </remarks>
public interface IIgnoredFaceRepository
{
    Task AddAsync(IReadOnlyList<FaceId> faceIds, CancellationToken ct);

    Task RemoveAllAsync(CancellationToken ct);

    Task<IReadOnlySet<FaceId>> GetAllAsync(CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}
