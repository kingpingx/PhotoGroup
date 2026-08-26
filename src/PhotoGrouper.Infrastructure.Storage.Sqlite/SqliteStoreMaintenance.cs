using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>Whole-store operations for the SQLite backend.</summary>
public sealed class SqliteStoreMaintenance(SqliteConnectionFactory connections) : IStoreMaintenance
{
    /// <summary>
    /// Emptied in dependency order, children before parents.
    /// </summary>
    /// <remarks>
    /// Deleting the rows rather than the file. The file is open and pooled while the application
    /// runs, so removing it would either fail outright or leave the connection pool holding a
    /// handle to something that no longer exists. Emptying the tables leaves the schema intact and
    /// needs no restart.
    ///
    /// The order matters because foreign keys are enforced. Listing them explicitly, rather than
    /// disabling the constraints for the duration, means a table added later without being added
    /// here fails loudly instead of silently surviving a reset.
    /// </remarks>
    private static readonly string[] TablesInDeletionOrder =
    [
        "face_links",
        "ignored_faces",
        "face_embeddings",
        "photo_detections",
        "export_ops",
        "export_runs",
        "faces",
        "clusters",
        "persons",
        "photos",
        "scan_roots",
        "settings",
    ];

    public async Task ClearAllAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();

        await using (var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            foreach (var table in TablesInDeletionOrder)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = $"DELETE FROM {table};";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // Reclaiming the space takes three steps, and skipping any of them leaves the library
        // larger after a reset than it was before.
        //
        // In write-ahead logging mode a delete does not shrink anything: every removed page is
        // appended to the log, so emptying a library of any size makes the total on disk grow.
        // Checkpointing with TRUNCATE folds the log back into the database and then discards it.
        await CheckpointAsync(connection, ct).ConfigureAwait(false);

        // The database file itself is still at its old size, now full of free pages. VACUUM
        // rebuilds it compactly. It cannot run inside a transaction, hence the separate statement.
        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // VACUUM itself writes the rebuilt database through the log, so without a second
        // checkpoint the space comes back in the database file and reappears in the log.
        await CheckpointAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when there is nothing left to clear.</summary>
    public async Task<bool> IsEmptyAsync(CancellationToken ct)
    {
        var contents = await DescribeAsync(ct).ConfigureAwait(false);
        return contents is { Photos: 0, Faces: 0, Embeddings: 0, People: 0, Clusters: 0, ScanRoots: 0 };
    }

    public async Task<StoreContents> DescribeAsync(CancellationToken ct)
    {
        await using var connection = connections.Open();

        return new StoreContents(
            await CountAsync(connection, "photos", ct).ConfigureAwait(false),
            await CountAsync(connection, "faces", ct).ConfigureAwait(false),
            await CountAsync(connection, "face_embeddings", ct).ConfigureAwait(false),
            await CountAsync(connection, "persons", ct).ConfigureAwait(false),
            await CountAsync(connection, "clusters", ct).ConfigureAwait(false),
            await CountAsync(connection, "scan_roots", ct).ConfigureAwait(false),
            SizeOnDisk());
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <remarks>
    /// The write-ahead log is counted alongside the database file. It routinely grows to tens of
    /// megabytes during a scan, and reporting only the main file would understate what the
    /// library actually occupies.
    /// </remarks>
    private long SizeOnDisk()
    {
        long total = 0;

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = connections.DatabasePath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // A file being written to right now is not worth failing a size report over.
                }
            }
        }

        return total;
    }
}
