using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for the folders the library indexes.</summary>
public sealed class SqliteScanRootRepository(SqliteConnectionFactory connections) : IScanRootRepository
{
    private const string SelectColumns = "id, path, recursive, is_implicit, last_scan_utc";

    public async Task<IReadOnlyList<ScanRoot>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM scan_roots ORDER BY path;";

        var results = new List<ScanRoot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ScanRoot?> GetByPathAsync(string path, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM scan_roots WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(ScanRoot root, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_roots (id, path, recursive, is_implicit, last_scan_utc)
            VALUES ($id, $path, $recursive, $implicit, $last)
            ON CONFLICT (path) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(root.Id.Value));
        command.Parameters.AddWithValue("$path", root.Path);
        command.Parameters.AddWithValue("$recursive", root.Recursive ? 1 : 0);
        command.Parameters.AddWithValue("$implicit", root.IsImplicit ? 1 : 0);
        command.Parameters.AddWithValue("$last", root.LastScanUtc is { } last ? SqliteMappings.ToDb(last) : DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(ScanRootId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        // Only the root is forgotten. Its photos stay indexed, because a user removing a
        // folder from the scan list has not said anything about wanting the people they
        // already named in those photos to disappear.
        command.CommandText = "DELETE FROM scan_roots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkScannedAsync(ScanRootId id, DateTimeOffset whenUtc, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scan_roots SET last_scan_utc = $when WHERE id = $id;";
        command.Parameters.AddWithValue("$when", SqliteMappings.ToDb(whenUtc));
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ScanRoot Map(SqliteDataReader reader) => new(
        new ScanRootId(reader.GetIdGuid(0)),
        reader.GetString(1),
        reader.GetInt32(2) != 0,
        reader.GetInt32(3) != 0,
        reader.GetNullableDateTimeOffset(4));
}
