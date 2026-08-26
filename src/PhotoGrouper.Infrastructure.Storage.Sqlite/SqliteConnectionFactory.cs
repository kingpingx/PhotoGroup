using Microsoft.Data.Sqlite;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>Opens connections to the library database.</summary>
public sealed class SqliteConnectionFactory(string databasePath)
{
    public string DatabasePath { get; } = databasePath;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());

        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL lets the UI read while a scan writes; without it the grid stalls behind every
        // batch insert. NORMAL trades a fsync per commit for durability only against OS
        // crash, not process crash, which is the right trade for a rebuildable index.
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();

        return connection;
    }
}
