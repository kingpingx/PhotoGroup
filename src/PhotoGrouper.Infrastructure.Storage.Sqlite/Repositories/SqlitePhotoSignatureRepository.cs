using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for photograph fingerprints.</summary>
public sealed class SqlitePhotoSignatureRepository(SqliteConnectionFactory connections)
    : IPhotoSignatureRepository
{
    private const string PhotoColumns =
        "p.id, p.path, p.file_size, p.mtime_utc, p.content_hash, p.width, p.height, " +
        "p.orientation, p.taken_utc, p.camera, p.state, p.indexed_utc, p.error";

    public async Task BulkUpsertAsync(IReadOnlyList<PhotoSignature> signatures, CancellationToken ct)
    {
        if (signatures.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO photo_signatures (photo_id, hash_across, hash_down, sharpness, computed_utc)
            VALUES ($photo, $across, $down, $sharpness, $computed)
            ON CONFLICT (photo_id) DO UPDATE SET
                hash_across  = excluded.hash_across,
                hash_down    = excluded.hash_down,
                sharpness    = excluded.sharpness,
                computed_utc = excluded.computed_utc;
            """;

        var photo = command.Parameters.Add(new SqliteParameter("$photo", DBNull.Value));
        var across = command.Parameters.Add(new SqliteParameter("$across", DBNull.Value));
        var down = command.Parameters.Add(new SqliteParameter("$down", DBNull.Value));
        var sharpness = command.Parameters.Add(new SqliteParameter("$sharpness", DBNull.Value));
        var computed = command.Parameters.Add(
            new SqliteParameter("$computed", DateTimeOffset.UtcNow.ToString("O")));

        foreach (var signature in signatures)
        {
            ct.ThrowIfCancellationRequested();
            photo.Value = SqliteMappings.ToDb(signature.PhotoId.Value);

            // Reinterpreted rather than converted. SQLite's INTEGER is signed, the fingerprint is
            // not, and a checked conversion would throw on every fingerprint whose top bit is set —
            // half of them.
            across.Value = unchecked((long)signature.Hash.Gradient);
            down.Value = unchecked((long)signature.Hash.Brightness);
            sharpness.Value = signature.Sharpness;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// A photograph that failed to decode is left out. It has no pixels to fingerprint, so
    /// including it would mean handing the same unreadable file to the decoder on every run and
    /// never making progress.
    /// </remarks>
    public async Task<IReadOnlyList<Photo>> GetPhotosNeedingSignatureAsync(int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {PhotoColumns}
            FROM photos p
            LEFT JOIN photo_signatures s ON s.photo_id = p.id
            WHERE s.photo_id IS NULL AND p.error IS NULL
            ORDER BY p.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Photo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapPhoto(reader));
        }

        return results;
    }

    public async Task<int> CountPhotosNeedingSignatureAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM photos p
            LEFT JOIN photo_signatures s ON s.photo_id = p.id
            WHERE s.photo_id IS NULL AND p.error IS NULL;
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<PhotoSignature>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT photo_id, hash_across, hash_down, sharpness FROM photo_signatures ORDER BY photo_id;";

        var results = new List<PhotoSignature>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new PhotoSignature(
                new PhotoId(reader.GetIdGuid(0)),
                new PerceptualHash(
                    unchecked((ulong)reader.GetInt64(1)),
                    unchecked((ulong)reader.GetInt64(2))),
                reader.GetDouble(3)));
        }

        return results;
    }

    private static Photo MapPhoto(SqliteDataReader reader) => new(
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
