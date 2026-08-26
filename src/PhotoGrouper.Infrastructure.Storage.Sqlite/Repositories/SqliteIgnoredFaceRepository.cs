using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed record of faces the user has dismissed.</summary>
public sealed class SqliteIgnoredFaceRepository(SqliteConnectionFactory connections) : IIgnoredFaceRepository
{
    public async Task AddAsync(IReadOnlyList<FaceId> faceIds, CancellationToken ct)
    {
        if (faceIds.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO ignored_faces (face_id, created_utc) VALUES ($id, $when)
            ON CONFLICT (face_id) DO NOTHING;
            """;

        var id = command.Parameters.Add(new SqliteParameter("$id", DBNull.Value));
        command.Parameters.AddWithValue("$when", SqliteMappings.ToDb(DateTimeOffset.UtcNow));

        foreach (var faceId in faceIds)
        {
            ct.ThrowIfCancellationRequested();
            id.Value = SqliteMappings.ToDb(faceId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ignored_faces;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<FaceId>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT face_id FROM ignored_faces;";

        var results = new HashSet<FaceId>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new FaceId(reader.GetIdGuid(0)));
        }

        return results;
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ignored_faces;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
