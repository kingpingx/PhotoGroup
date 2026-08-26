using PhotoGrouper.Application.Ports;
using PhotoGrouper.Contracts.Tests;
using PhotoGrouper.Infrastructure.Storage.Sqlite;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Runs the storage contract against the SQLite adapter.
/// </summary>
/// <remarks>
/// There is deliberately no test body here. Everything asserted lives in the shared contract,
/// so a second backend would consist of exactly this much code and would either pass or be
/// revealed as not a drop-in replacement.
/// </remarks>
public sealed class SqlitePhotoRepositoryTests : PhotoRepositoryContract, IDisposable
{
    private readonly TemporaryDatabase _database = new();

    protected override Task<(IPhotoReader Reader, IPhotoWriter Writer)> CreateAsync()
    {
        var repository = new SqlitePhotoRepository(_database.Connections);
        return Task.FromResult<(IPhotoReader, IPhotoWriter)>((repository, repository));
    }

    public void Dispose() => _database.Dispose();
}

public sealed class SqliteScanRootRepositoryTests : ScanRootRepositoryContract, IDisposable
{
    private readonly TemporaryDatabase _database = new();

    protected override Task<IScanRootRepository> CreateAsync() =>
        Task.FromResult<IScanRootRepository>(new SqliteScanRootRepository(_database.Connections));

    public void Dispose() => _database.Dispose();
}

/// <summary>A migrated database in a temporary file, deleted when the test finishes.</summary>
/// <remarks>
/// A file rather than an in-memory database because the point is to exercise the real thing:
/// WAL mode, file locking and on-disk types all behave differently in memory, and those are
/// precisely the areas where a storage adapter goes wrong.
/// </remarks>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"photogrouper-tests-{Guid.NewGuid():N}.db");

    public TemporaryDatabase()
    {
        Connections = new SqliteConnectionFactory(_path);
        new SqliteStore(Connections).Initialize();
    }

    public SqliteConnectionFactory Connections { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leaked handle should not fail an otherwise passing test run; the temp
                // directory is cleaned up by the OS regardless.
            }
        }
    }
}
