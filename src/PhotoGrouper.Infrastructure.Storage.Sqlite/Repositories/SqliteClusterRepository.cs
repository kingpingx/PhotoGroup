using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for algorithmic face groups.</summary>
public sealed class SqliteClusterRepository(SqliteConnectionFactory connections) : IClusterRepository
{
    private const string SelectColumns =
        "id, detector_id, embedder_id, size, medoid_face_id, created_utc, person_id";

    /// <remarks>
    /// The delete and the insert share one transaction. Between them the library has no groups at
    /// all, and a crash in the gap would leave a user with an empty People page and no indication
    /// that re-running the grouping would restore it.
    /// </remarks>
    public async Task ReplaceAllAsync(
        string detectorId, string embedderId, IReadOnlyList<ClusterRecord> clusters, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM clusters WHERE detector_id = $detector AND embedder_id = $embedder;";
            delete.Parameters.AddWithValue("$detector", detectorId);
            delete.Parameters.AddWithValue("$embedder", embedderId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (clusters.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO clusters (id, detector_id, embedder_id, person_id, size, medoid_face_id, created_utc)
                VALUES ($id, $detector, $embedder, $person, $size, $medoid, $created);
                """;

            var id = insert.Parameters.Add(new SqliteParameter("$id", DBNull.Value));
            insert.Parameters.AddWithValue("$detector", detectorId);
            insert.Parameters.AddWithValue("$embedder", embedderId);
            var person = insert.Parameters.Add(new SqliteParameter("$person", DBNull.Value));
            var size = insert.Parameters.Add(new SqliteParameter("$size", DBNull.Value));
            var medoid = insert.Parameters.Add(new SqliteParameter("$medoid", DBNull.Value));
            var created = insert.Parameters.Add(new SqliteParameter("$created", DBNull.Value));

            foreach (var cluster in clusters)
            {
                ct.ThrowIfCancellationRequested();
                id.Value = SqliteMappings.ToDb(cluster.Id.Value);
                person.Value = cluster.PersonId is { } p ? SqliteMappings.ToDb(p.Value) : DBNull.Value;
                size.Value = cluster.Size;
                medoid.Value = SqliteMappings.ToDb(cluster.MedoidFaceId.Value);
                created.Value = SqliteMappings.ToDb(cluster.CreatedUtc);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClusterRecord>> GetAllAsync(
        string detectorId, string embedderId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM clusters " +
            "WHERE detector_id = $detector AND embedder_id = $embedder ORDER BY size DESC, id;";
        command.Parameters.AddWithValue("$detector", detectorId);
        command.Parameters.AddWithValue("$embedder", embedderId);

        var results = new List<ClusterRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ClusterRecord?> GetByIdAsync(ClusterId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM clusters WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task SetPersonAsync(ClusterId id, PersonId? personId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clusters SET person_id = $person WHERE id = $id;";
        command.Parameters.AddWithValue(
            "$person", personId is { } p ? SqliteMappings.ToDb(p.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearPersonAsync(PersonId personId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clusters SET person_id = NULL WHERE person_id = $person;";
        command.Parameters.AddWithValue("$person", SqliteMappings.ToDb(personId.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ClusterRecord Map(SqliteDataReader reader) => new(
        new ClusterId(reader.GetIdGuid(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        new FaceId(reader.GetIdGuid(4)),
        reader.GetDateTimeOffset(5),
        reader.IsDBNull(6) ? null : new PersonId(reader.GetIdGuid(6)));
}
