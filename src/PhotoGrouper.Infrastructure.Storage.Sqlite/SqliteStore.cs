using PhotoGrouper.Infrastructure.Storage.Sqlite.Migrations;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>Prepares the SQLite database for use.</summary>
/// <remarks>
/// Called once from the composition root at startup. Kept as its own type so the app does
/// not have to know that "initialise the store" means "run DDL" for this particular backend.
/// </remarks>
public sealed class SqliteStore(SqliteConnectionFactory connections)
{
    public void Initialize()
    {
        var directory = Path.GetDirectoryName(connections.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        new SchemaMigrator(connections).Migrate();
    }
}
