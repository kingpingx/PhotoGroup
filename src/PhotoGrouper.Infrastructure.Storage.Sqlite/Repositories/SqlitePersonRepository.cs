using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for named people.</summary>
public sealed class SqlitePersonRepository(SqliteConnectionFactory connections) : IPersonRepository
{
    private const string SelectColumns = "id, display_name, cover_face_id, centroid, created_utc";

    public async Task<IReadOnlyList<Person>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM persons ORDER BY display_name COLLATE NOCASE;";

        var results = new List<Person>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<Person?> GetByIdAsync(PersonId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM persons WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<Person?> GetByNameAsync(PersonName name, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM persons WHERE display_name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(Person person, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO persons (id, display_name, cover_face_id, centroid, created_utc)
            VALUES ($id, $name, $cover, $centroid, $created);
            """;
        Bind(command, person);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Person person, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE persons
            SET display_name = $name, cover_face_id = $cover, centroid = $centroid
            WHERE id = $id;
            """;
        Bind(command, person);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// The schema detaches this person's faces rather than cascading the delete. Removing a name
    /// means the grouping was wrong, not that the photographs stopped containing a face, and
    /// deleting the rows would discard detection work along with the mistake.
    /// </remarks>
    public async Task RemoveAsync(PersonId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM persons WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, Person person)
    {
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(person.Id.Value));
        command.Parameters.AddWithValue("$name", person.Name.Value);
        command.Parameters.AddWithValue(
            "$cover", person.CoverFaceId is { } cover ? SqliteMappings.ToDb(cover.Value) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$centroid", person.Centroid is { } centroid ? VectorCodec.ToBytes(centroid) : DBNull.Value);
        command.Parameters.AddWithValue("$created", SqliteMappings.ToDb(person.CreatedUtc));
    }

    private static Person Map(SqliteDataReader reader) => new(
        new PersonId(reader.GetIdGuid(0)),
        PersonName.Create(reader.GetString(1)),
        reader.GetDateTimeOffset(4),
        reader.IsDBNull(2) ? null : new FaceId(reader.GetIdGuid(2)),
        reader.IsDBNull(3) ? null : VectorCodec.FromBytes((byte[])reader["centroid"]));
}

/// <summary>Serialises float vectors to and from blob columns.</summary>
/// <remarks>
/// Explicitly little-endian rather than through BitConverter's platform default: a library file
/// is expected to be portable, and a stored value whose meaning depends on the machine that
/// wrote it is not.
/// </remarks>
internal static class VectorCodec
{
    public static byte[] ToBytes(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"A float vector blob must be a multiple of {sizeof(float)} bytes.", nameof(bytes));
        }

        var values = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return values;
    }
}
