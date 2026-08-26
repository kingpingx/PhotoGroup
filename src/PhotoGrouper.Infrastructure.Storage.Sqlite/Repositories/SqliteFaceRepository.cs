using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for detected faces.</summary>
public sealed class SqliteFaceRepository(SqliteConnectionFactory connections) : IFaceRepository
{
    private const string SelectColumns =
        "id, photo_id, detector_id, detector_version, active, bbox_x, bbox_y, bbox_w, bbox_h, " +
        "det_score, landmarks, blur_score, cluster_id, person_id, assignment";

    public async Task BulkInsertAsync(IReadOnlyList<Face> faces, CancellationToken ct)
    {
        if (faces.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO faces (id, photo_id, detector_id, detector_version, active,
                               bbox_x, bbox_y, bbox_w, bbox_h, det_score, landmarks,
                               blur_score, face_px, cluster_id, person_id, assignment)
            VALUES ($id, $photo, $detector, $version, $active,
                    $x, $y, $w, $h, $score, $landmarks,
                    $blur, $facePx, $cluster, $person, $assignment);
            """;

        var p = CreateParameters(command);

        foreach (var face in faces)
        {
            ct.ThrowIfCancellationRequested();
            Fill(p, face);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Face>> GetByPhotoAsync(PhotoId photoId, string detectorId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM faces WHERE photo_id = $photo AND detector_id = $detector ORDER BY id;";
        command.Parameters.AddWithValue("$photo", SqliteMappings.ToDb(photoId.Value));
        command.Parameters.AddWithValue("$detector", detectorId);

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Parameters are generated for the id list rather than the values being concatenated into the
    /// statement. Chunked because SQLite caps how many a single statement may carry, and a batch
    /// larger than that would fail only once a library grew big enough to produce one.
    /// </remarks>
    public async Task<IReadOnlyList<Face>> GetByIdsAsync(IReadOnlyList<FaceId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        const int chunkSize = 400;
        var results = new List<Face>(ids.Count);

        await using var connection = connections.Open();

        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = ids.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select((_, i) => $"$id{i}"));

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM faces WHERE id IN ({placeholders});";

            for (var i = 0; i < chunk.Count; i++)
            {
                command.Parameters.AddWithValue($"$id{i}", SqliteMappings.ToDb(chunk[i].Value));
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(Map(reader));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<Face>> GetByPersonAsync(PersonId personId, string detectorId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        // Filtered to active faces as well as to the detector: an inactive set from a previous
        // detector still carries person assignments, and including them would report every
        // person's photo count as roughly double.
        command.CommandText =
            $"SELECT {SelectColumns} FROM faces " +
            "WHERE person_id = $person AND detector_id = $detector AND active = 1 ORDER BY id;";
        command.Parameters.AddWithValue("$person", SqliteMappings.ToDb(personId.Value));
        command.Parameters.AddWithValue("$detector", detectorId);

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(string detectorId, bool activeOnly, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? "SELECT COUNT(*) FROM faces WHERE detector_id = $detector AND active = 1;"
            : "SELECT COUNT(*) FROM faces WHERE detector_id = $detector;";
        command.Parameters.AddWithValue("$detector", detectorId);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task DeleteByPhotoAsync(PhotoId photoId, string detectorId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM faces WHERE photo_id = $photo AND detector_id = $detector;";
        command.Parameters.AddWithValue("$photo", SqliteMappings.ToDb(photoId.Value));
        command.Parameters.AddWithValue("$detector", detectorId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AssignAsync(IReadOnlyList<FaceAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            "UPDATE faces SET person_id = $person, assignment = $assignment WHERE id = $id;";

        var person = command.Parameters.Add(new SqliteParameter("$person", DBNull.Value));
        var assignment = command.Parameters.Add(new SqliteParameter("$assignment", DBNull.Value));
        var id = command.Parameters.Add(new SqliteParameter("$id", DBNull.Value));

        foreach (var item in assignments)
        {
            ct.ThrowIfCancellationRequested();
            person.Value = item.PersonId is { } p ? SqliteMappings.ToDb(p.Value) : DBNull.Value;
            assignment.Value = (int)item.Assignment;
            id.Value = SqliteMappings.ToDb(item.FaceId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetClustersAsync(IReadOnlyList<FaceClusterAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE faces SET cluster_id = $cluster WHERE id = $id;";

        var cluster = command.Parameters.Add(new SqliteParameter("$cluster", DBNull.Value));
        var id = command.Parameters.Add(new SqliteParameter("$id", DBNull.Value));

        foreach (var assignment in assignments)
        {
            ct.ThrowIfCancellationRequested();
            cluster.Value = assignment.ClusterId is { } c ? SqliteMappings.ToDb(c.Value) : DBNull.Value;
            id.Value = SqliteMappings.ToDb(assignment.FaceId.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Face>> GetByClusterAsync(ClusterId clusterId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM faces WHERE cluster_id = $cluster ORDER BY id;";
        command.Parameters.AddWithValue("$cluster", SqliteMappings.ToDb(clusterId.Value));

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// A single statement rather than a delete and reinsert. The previous detector's faces keep
    /// their person assignments while inactive, which is what makes switching back to it instant
    /// and lossless instead of another full pass over the library.
    /// </remarks>
    public async Task SetActiveDetectorAsync(string detectorId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE faces SET active = CASE WHEN detector_id = $detector THEN 1 ELSE 0 END;";
        command.Parameters.AddWithValue("$detector", detectorId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<Face> StreamByDetectorAsync(
        string detectorId, bool activeOnly, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? $"SELECT {SelectColumns} FROM faces WHERE detector_id = $detector AND active = 1 ORDER BY id;"
            : $"SELECT {SelectColumns} FROM faces WHERE detector_id = $detector ORDER BY id;";
        command.Parameters.AddWithValue("$detector", detectorId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return Map(reader);
        }
    }

    private static async Task<IReadOnlyList<Face>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<Face>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static SqliteParameter[] CreateParameters(SqliteCommand command)
    {
        string[] names =
        [
            "$id", "$photo", "$detector", "$version", "$active", "$x", "$y", "$w", "$h",
            "$score", "$landmarks", "$blur", "$facePx", "$cluster", "$person", "$assignment",
        ];

        var parameters = new SqliteParameter[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            parameters[i] = command.Parameters.Add(new SqliteParameter(names[i], DBNull.Value));
        }

        return parameters;
    }

    private static void Fill(SqliteParameter[] p, Face face)
    {
        p[0].Value = SqliteMappings.ToDb(face.Id.Value);
        p[1].Value = SqliteMappings.ToDb(face.PhotoId.Value);
        p[2].Value = face.DetectorId;
        p[3].Value = face.DetectorVersion;
        p[4].Value = face.IsActive ? 1 : 0;
        p[5].Value = face.Box.X;
        p[6].Value = face.Box.Y;
        p[7].Value = face.Box.Width;
        p[8].Value = face.Box.Height;
        p[9].Value = face.Box.Score;
        p[10].Value = LandmarkCodec.ToBytes(face.Landmarks);
        p[11].Value = (object?)face.BlurScore ?? DBNull.Value;
        p[12].Value = face.FacePixels;
        p[13].Value = face.ClusterId is { } c ? SqliteMappings.ToDb(c.Value) : DBNull.Value;
        p[14].Value = face.PersonId is { } person ? SqliteMappings.ToDb(person.Value) : DBNull.Value;
        p[15].Value = (int)face.Assignment;
    }

    private static Face Map(SqliteDataReader reader)
    {
        var landmarkBytes = (byte[])reader["landmarks"];

        return new Face(
            new FaceId(reader.GetIdGuid(0)),
            new PhotoId(reader.GetIdGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            new FaceBox(
                (float)reader.GetDouble(5),
                (float)reader.GetDouble(6),
                (float)reader.GetDouble(7),
                (float)reader.GetDouble(8),
                (float)reader.GetDouble(9)),
            LandmarkCodec.FromBytes(landmarkBytes),
            reader.GetInt32(4) != 0,
            reader.IsDBNull(11) ? null : (float)reader.GetDouble(11),
            reader.IsDBNull(13) ? null : new PersonId(reader.GetIdGuid(13)),
            reader.IsDBNull(12) ? null : new ClusterId(reader.GetIdGuid(12)),
            (Assignment)reader.GetInt32(14));
    }
}
