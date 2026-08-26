using Microsoft.Data.Sqlite;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>Conversions between domain values and their SQLite representation.</summary>
internal static class SqliteMappings
{
    /// <summary>
    /// Writes a Guid as 16 big-endian bytes.
    /// </summary>
    /// <remarks>
    /// Guid.ToByteArray stores the first three fields little-endian, which would put the
    /// UUIDv7 timestamp bytes in the wrong order and destroy the index locality that
    /// choosing version 7 was meant to buy. Every id crossing this boundary goes through
    /// here so the ordering holds.
    /// </remarks>
    public static byte[] ToDb(Guid id) => Uuid7.ToBigEndian(id);

    public static Guid GuidFromDb(byte[] bytes) => Uuid7.FromBigEndian(bytes);

    public static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public static DateTimeOffset DateTimeFromDb(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    /// <summary>Reads a 16-byte id column written by <see cref="ToDb(Guid)"/>.</summary>
    /// <remarks>
    /// Named to avoid colliding with DbDataReader.GetGuid. An extension method never wins
    /// against an instance method of the same name, so calling GetGuid here would silently
    /// bind to the built-in one, which reads the blob in platform byte order and hands back
    /// a different id than was written.
    /// </remarks>
    public static Guid GetIdGuid(this SqliteDataReader reader, int ordinal)
    {
        var bytes = new byte[16];
        var read = reader.GetBytes(ordinal, 0, bytes, 0, 16);
        if (read != 16)
        {
            throw new InvalidOperationException($"Expected a 16 byte id but read {read} bytes.");
        }

        return GuidFromDb(bytes);
    }

    public static DateTimeOffset GetDateTimeOffset(this SqliteDataReader reader, int ordinal) =>
        DateTimeFromDb(reader.GetString(ordinal));

    public static DateTimeOffset? GetNullableDateTimeOffset(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeFromDb(reader.GetString(ordinal));

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int? GetNullableInt32(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
