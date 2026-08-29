using Microsoft.Data.Sqlite;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Migrations;

/// <summary>
/// Brings the database schema up to the version this build expects.
/// </summary>
/// <remarks>
/// Owned by the storage adapter, not the application: what "migrate" means is entirely a
/// property of the backend. A document store would create indexes here instead of running
/// DDL, behind the same call from the composition root.
/// </remarks>
public sealed class SchemaMigrator(SqliteConnectionFactory connections)
{
    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, "initial", SqlScripts.V1Initial),
        new(2, "photo-detections", SqlScripts.V2PhotoDetections),
        new(3, "ignored-faces", SqlScripts.V3IgnoredFaces),
        new(4, "photo-signatures", SqlScripts.V4PhotoSignatures),
    ];

    public int CurrentVersion { get; private set; }

    public void Migrate()
    {
        using var connection = connections.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_utc TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var applied = ReadAppliedVersion(connection);

        foreach (var migration in Migrations.Where(m => m.Version > applied).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (version, applied_utc) VALUES ($v, $t);";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            applied = migration.Version;
        }

        CurrentVersion = applied;
    }

    private static int ReadAppliedVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
