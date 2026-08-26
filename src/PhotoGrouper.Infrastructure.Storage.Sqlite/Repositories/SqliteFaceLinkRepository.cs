using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for review decisions.</summary>
public sealed class SqliteFaceLinkRepository(SqliteConnectionFactory connections) : IFaceLinkRepository
{
    public async Task<IReadOnlyList<FaceLink>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT face_a, face_b, kind FROM face_links;";

        var results = new List<FaceLink>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new FaceLink(
                new FaceId(reader.GetIdGuid(0)),
                new FaceId(reader.GetIdGuid(1)),
                (FaceLinkKind)reader.GetInt32(2)));
        }

        return results;
    }

    /// <remarks>
    /// The pair is ordered before it is written, and the schema enforces that ordering. Recording
    /// the same two faces in the opposite order therefore updates the existing decision rather
    /// than adding a second, contradictory one; without it a user who answered a question twice
    /// could leave the database asserting that two faces both must and cannot be the same person.
    /// </remarks>
    public async Task AddAsync(FaceId a, FaceId b, FaceLinkKind kind, CancellationToken ct)
    {
        if (a == b)
        {
            throw new ArgumentException("A face cannot be linked to itself.", nameof(b));
        }

        var (first, second) = Order(a, b);

        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO face_links (face_a, face_b, kind, created_utc)
            VALUES ($a, $b, $kind, $created)
            ON CONFLICT (face_a, face_b) DO UPDATE SET
                kind = excluded.kind,
                created_utc = excluded.created_utc;
            """;
        command.Parameters.AddWithValue("$a", first);
        command.Parameters.AddWithValue("$b", second);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$created", SqliteMappings.ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(FaceId a, FaceId b, CancellationToken ct)
    {
        var (first, second) = Order(a, b);

        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM face_links WHERE face_a = $a AND face_b = $b;";
        command.Parameters.AddWithValue("$a", first);
        command.Parameters.AddWithValue("$b", second);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM face_links;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Puts a pair into the order the schema requires.
    /// </summary>
    /// <remarks>
    /// Compared as stored bytes rather than by the Guid type's own ordering, which sorts its first
    /// three fields in a way that does not match their byte sequence. The check constraint in the
    /// database compares blobs, so this must agree with that and not with .NET's convention.
    /// </remarks>
    private static (byte[] First, byte[] Second) Order(FaceId a, FaceId b)
    {
        var left = SqliteMappings.ToDb(a.Value);
        var right = SqliteMappings.ToDb(b.Value);

        return ((ReadOnlySpan<byte>)left).SequenceCompareTo(right) <= 0
            ? (left, right)
            : (right, left);
    }
}
