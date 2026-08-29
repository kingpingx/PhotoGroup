using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for face embeddings.</summary>
public sealed class SqliteEmbeddingRepository(SqliteConnectionFactory connections) : IEmbeddingRepository
{
    public async Task BulkUpsertAsync(
        string embedderId,
        string embedderVersion,
        IReadOnlyList<FaceEmbedding> embeddings,
        CancellationToken ct)
    {
        if (embeddings.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO face_embeddings (face_id, embedder_id, embedder_version, dim, vector)
            VALUES ($face, $embedder, $version, $dim, $vector)
            ON CONFLICT (face_id, embedder_id) DO UPDATE SET
                embedder_version = excluded.embedder_version,
                dim              = excluded.dim,
                vector           = excluded.vector;
            """;

        var face = command.Parameters.Add(new SqliteParameter("$face", DBNull.Value));
        var embedder = command.Parameters.Add(new SqliteParameter("$embedder", embedderId));
        var version = command.Parameters.Add(new SqliteParameter("$version", embedderVersion));
        var dim = command.Parameters.Add(new SqliteParameter("$dim", DBNull.Value));
        var vector = command.Parameters.Add(new SqliteParameter("$vector", DBNull.Value));

        foreach (var embedding in embeddings)
        {
            ct.ThrowIfCancellationRequested();
            face.Value = SqliteMappings.ToDb(embedding.FaceId.Value);
            dim.Value = embedding.Vector.Length;
            vector.Value = VectorCodec.ToBytes(embedding.Vector);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<float[]?> GetAsync(FaceId faceId, string embedderId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT vector FROM face_embeddings WHERE face_id = $face AND embedder_id = $embedder;";
        command.Parameters.AddWithValue("$face", SqliteMappings.ToDb(faceId.Value));
        command.Parameters.AddWithValue("$embedder", embedderId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? VectorCodec.FromBytes((byte[])reader["vector"])
            : null;
    }

    /// <remarks>
    /// Scoped to the active detector as well as the embedder. Faces belonging to an inactive
    /// detector are retained so that switching back is instant, but embedding them would mean
    /// paying tens of milliseconds each for vectors nothing is going to read.
    /// </remarks>
    /// <remarks>
    /// Parameters are generated for the id list rather than the values being concatenated into the
    /// statement, and chunked because SQLite caps how many a single statement may carry — the same
    /// shape, and the same reasoning, as fetching faces by id.
    ///
    /// One connection for the whole call, not one per chunk. The point of this method is that
    /// recomputing a person stops being a few hundred round trips.
    /// </remarks>
    public async Task<IReadOnlyList<FaceEmbedding>> GetManyAsync(
        IReadOnlyList<FaceId> faceIds, string embedderId, CancellationToken ct)
    {
        if (faceIds.Count == 0)
        {
            return [];
        }

        const int chunkSize = 400;
        var results = new List<FaceEmbedding>(faceIds.Count);

        await using var connection = connections.Open();

        for (var offset = 0; offset < faceIds.Count; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = faceIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select((_, i) => $"$id{i}"));

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT face_id, vector FROM face_embeddings " +
                $"WHERE embedder_id = $embedder AND face_id IN ({placeholders});";

            command.Parameters.AddWithValue("$embedder", embedderId);
            for (var i = 0; i < chunk.Count; i++)
            {
                command.Parameters.AddWithValue($"$id{i}", SqliteMappings.ToDb(chunk[i].Value));
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new FaceEmbedding(
                    new FaceId(reader.GetIdGuid(0)),
                    VectorCodec.FromBytes((byte[])reader["vector"])));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<FaceId>> GetFacesMissingEmbeddingAsync(
        string embedderId, string detectorId, int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id
            FROM faces f
            LEFT JOIN face_embeddings e ON e.face_id = f.id AND e.embedder_id = $embedder
            WHERE f.detector_id = $detector AND f.active = 1 AND e.face_id IS NULL
            ORDER BY f.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$embedder", embedderId);
        command.Parameters.AddWithValue("$detector", detectorId);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<FaceId>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(new FaceId(reader.GetIdGuid(0)));
        }

        return ids;
    }

    public async Task<int> CountAsync(string embedderId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM face_embeddings WHERE embedder_id = $embedder;";
        command.Parameters.AddWithValue("$embedder", embedderId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<FaceEmbedding> StreamByEmbedderAsync(
        string embedderId, string detectorId, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.face_id, e.vector
            FROM face_embeddings e
            JOIN faces f ON f.id = e.face_id
            WHERE e.embedder_id = $embedder AND f.detector_id = $detector AND f.active = 1
            ORDER BY e.face_id;
            """;
        command.Parameters.AddWithValue("$embedder", embedderId);
        command.Parameters.AddWithValue("$detector", detectorId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new FaceEmbedding(
                new FaceId(reader.GetIdGuid(0)),
                VectorCodec.FromBytes((byte[])reader["vector"]));
        }
    }

    public async Task DeleteByEmbedderAsync(string embedderId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM face_embeddings WHERE embedder_id = $embedder;";
        command.Parameters.AddWithValue("$embedder", embedderId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
