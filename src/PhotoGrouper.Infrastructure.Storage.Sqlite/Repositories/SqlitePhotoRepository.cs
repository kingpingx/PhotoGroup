using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed read and write access to the photo index.</summary>
/// <remarks>
/// Implements both halves of the split port. They are separate interfaces so callers can
/// be handed only what they need; there is no reason for the implementation to be split
/// as well, since both share the same row mapping.
/// </remarks>
public sealed class SqlitePhotoRepository(SqliteConnectionFactory connections) : IPhotoReader, IPhotoWriter
{
    private const string SelectColumns =
        "id, path, file_size, mtime_utc, content_hash, width, height, orientation, taken_utc, camera, state, indexed_utc, error";

    public async Task<Photo?> GetByIdAsync(PhotoId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM photos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<Photo?> GetByPathAsync(string path, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM photos WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<Photo>> GetByStateAsync(PhotoState state, int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM photos WHERE state = $state ORDER BY id LIMIT $limit;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Photo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    /// <remarks>
    /// A photograph qualifies when this detector has no record for it, or when the file has
    /// changed since it was examined and has been reset to <see cref="PhotoState.New"/>. Files
    /// marked <see cref="PhotoState.Failed"/> are excluded: they could not be decoded before and
    /// will not decode now, and retrying them would make every run pay for them again.
    /// </remarks>
    private const string NeedsDetectionPredicate = """
        p.state <> $failed
        AND (p.state = $new OR d.photo_id IS NULL)
        """;

    public async Task<IReadOnlyList<Photo>> GetPhotosNeedingDetectionAsync(
        string detectorId, int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM photos p
            LEFT JOIN photo_detections d ON d.photo_id = p.id AND d.detector_id = $detector
            WHERE {NeedsDetectionPredicate}
            ORDER BY p.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$detector", detectorId);
        command.Parameters.AddWithValue("$failed", (int)PhotoState.Failed);
        command.Parameters.AddWithValue("$new", (int)PhotoState.New);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Photo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<int> CountPhotosNeedingDetectionAsync(string detectorId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM photos p
            LEFT JOIN photo_detections d ON d.photo_id = p.id AND d.detector_id = $detector
            WHERE {NeedsDetectionPredicate};
            """;
        command.Parameters.AddWithValue("$detector", detectorId);
        command.Parameters.AddWithValue("$failed", (int)PhotoState.Failed);
        command.Parameters.AddWithValue("$new", (int)PhotoState.New);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task RecordDetectionAsync(
        PhotoId id, string detectorId, string detectorVersion, int faceCount, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO photo_detections (photo_id, detector_id, detector_version, face_count, detected_utc)
            VALUES ($photo, $detector, $version, $count, $when)
            ON CONFLICT (photo_id, detector_id) DO UPDATE SET
                detector_version = excluded.detector_version,
                face_count       = excluded.face_count,
                detected_utc     = excluded.detected_utc;
            """;
        command.Parameters.AddWithValue("$photo", SqliteMappings.ToDb(id.Value));
        command.Parameters.AddWithValue("$detector", detectorId);
        command.Parameters.AddWithValue("$version", detectorVersion);
        command.Parameters.AddWithValue("$count", faceCount);
        command.Parameters.AddWithValue("$when", SqliteMappings.ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM photos;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async IAsyncEnumerable<Photo> StreamAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM photos ORDER BY id;";

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return Map(reader);
        }
    }

    public async Task<PhotoId> UpsertAsync(Photo photo, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        Fill(CreateParameters(command), photo);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return photo.Id;
    }

    public async Task BulkUpsertAsync(IReadOnlyList<Photo> photos, CancellationToken ct)
    {
        if (photos.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = UpsertSql;

        // Parameters are created once and only their values change per row. Rebuilding the
        // collection each iteration forces the statement to be re-prepared, which dominates
        // the cost when writing tens of thousands of rows.
        var parameters = CreateParameters(command);

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();
            Fill(parameters, photo);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetStateAsync(PhotoId id, PhotoState state, string? error, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE photos SET state = $state, error = $error, indexed_utc = $indexed WHERE id = $id;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexed", SqliteMappings.ToDb(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdatePathAsync(PhotoId id, string newPath, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE photos SET path = $path WHERE id = $id;";
        command.Parameters.AddWithValue("$path", newPath);
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateImageDetailsAsync(PhotoId id, ImageDetails details, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE photos
            SET width = $width, height = $height, orientation = $orientation,
                taken_utc = $taken, camera = $camera
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$width", details.Width);
        command.Parameters.AddWithValue("$height", details.Height);
        command.Parameters.AddWithValue("$orientation", details.Orientation);
        command.Parameters.AddWithValue(
            "$taken", details.TakenUtc is { } taken ? SqliteMappings.ToDb(taken) : DBNull.Value);
        command.Parameters.AddWithValue("$camera", (object?)details.Camera ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// The faces, embeddings and signature go with it, by the cascades declared in the schema
    /// rather than by four statements here that could fall out of step with them.
    /// </remarks>
    public async Task RemoveAsync(PhotoId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM photos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Conflict is resolved on path rather than id because the scanner identifies a file by
    /// where it lives. Re-indexing a known path must update that row, not insert a second
    /// one carrying a freshly generated id.
    /// </remarks>
    private const string UpsertSql = """
        INSERT INTO photos (id, path, file_size, mtime_utc, content_hash, width, height, orientation, taken_utc, camera, state, indexed_utc, error)
        VALUES ($id, $path, $file_size, $mtime, $hash, $width, $height, $orientation, $taken, $camera, $state, $indexed, $error)
        ON CONFLICT (path) DO UPDATE SET
            file_size    = excluded.file_size,
            mtime_utc    = excluded.mtime_utc,
            content_hash = excluded.content_hash,
            width        = excluded.width,
            height       = excluded.height,
            orientation  = excluded.orientation,
            taken_utc    = excluded.taken_utc,
            camera       = excluded.camera,
            state        = excluded.state,
            indexed_utc  = excluded.indexed_utc,
            error        = excluded.error;
        """;

    private static SqliteParameter[] CreateParameters(SqliteCommand command)
    {
        string[] names =
        [
            "$id", "$path", "$file_size", "$mtime", "$hash", "$width", "$height",
            "$orientation", "$taken", "$camera", "$state", "$indexed", "$error",
        ];

        var parameters = new SqliteParameter[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            parameters[i] = command.Parameters.Add(new SqliteParameter(names[i], DBNull.Value));
        }

        return parameters;
    }

    private static void Fill(SqliteParameter[] p, Photo photo)
    {
        p[0].Value = SqliteMappings.ToDb(photo.Id.Value);
        p[1].Value = photo.Path;
        p[2].Value = photo.FileSize;
        p[3].Value = SqliteMappings.ToDb(photo.ModifiedUtc);
        p[4].Value = (object?)photo.ContentHash?.Value ?? DBNull.Value;
        p[5].Value = (object?)photo.Width ?? DBNull.Value;
        p[6].Value = (object?)photo.Height ?? DBNull.Value;
        p[7].Value = photo.Orientation;
        p[8].Value = photo.TakenUtc is { } taken ? SqliteMappings.ToDb(taken) : DBNull.Value;
        p[9].Value = (object?)photo.Camera ?? DBNull.Value;
        p[10].Value = (int)photo.State;
        p[11].Value = photo.IndexedUtc is { } indexed ? SqliteMappings.ToDb(indexed) : DBNull.Value;
        p[12].Value = (object?)photo.Error ?? DBNull.Value;
    }

    private static Photo Map(SqliteDataReader reader) => new(
        new PhotoId(reader.GetIdGuid(0)),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetDateTimeOffset(3),
        reader.GetNullableString(4) is { } hash ? new ContentHash(hash) : null,
        reader.GetNullableInt32(5),
        reader.GetNullableInt32(6),
        reader.GetInt32(7),
        reader.GetNullableDateTimeOffset(8),
        reader.GetNullableString(9),
        (PhotoState)reader.GetInt32(10),
        reader.GetNullableDateTimeOffset(11),
        reader.GetNullableString(12));
}
