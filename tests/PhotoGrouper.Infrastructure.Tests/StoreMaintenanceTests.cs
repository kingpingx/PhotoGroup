using FluentAssertions;
using PhotoGrouper.Domain.Common;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Identity;
using PhotoGrouper.Domain.People;
using PhotoGrouper.Domain.Photos;
using PhotoGrouper.Infrastructure.Storage.Sqlite;
using PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

namespace PhotoGrouper.Infrastructure.Tests;

/// <summary>
/// Covers clearing the store.
/// </summary>
/// <remarks>
/// A reset destroys the only data in the application nobody can recreate, so the tests that matter
/// are the ones proving it removes everything it claims to. A reset that quietly left one table
/// populated would be worse than none at all: the library would appear empty while stale rows
/// referenced photographs that no longer existed.
/// </remarks>
public sealed class SqliteStoreMaintenanceTests : IDisposable
{
    private readonly TemporaryDatabase _database = new();

    private SqliteStoreMaintenance Subject => new(_database.Connections);

    private static readonly FaceLandmarks Landmarks = new(
        new Point2(30, 40), new Point2(70, 40), new Point2(50, 60),
        new Point2(35, 80), new Point2(65, 80));

    /// <summary>Fills every table a reset is expected to empty.</summary>
    private async Task PopulateAsync()
    {
        var photos = new SqlitePhotoRepository(_database.Connections);
        var faces = new SqliteFaceRepository(_database.Connections);
        var people = new SqlitePersonRepository(_database.Connections);
        var embeddings = new SqliteEmbeddingRepository(_database.Connections);
        var roots = new SqliteScanRootRepository(_database.Connections);
        var links = new SqliteFaceLinkRepository(_database.Connections);

        await roots.AddAsync(new ScanRoot(ScanRootId.New(), @"D:\photos"), default);

        var photo = new Photo(PhotoId.New(), @"D:\photos\a.jpg", 1000, DateTimeOffset.UnixEpoch);
        await photos.UpsertAsync(photo, default);
        await photos.RecordDetectionAsync(photo.Id, "detector", "1", 2, default);

        var first = new Face(FaceId.New(), photo.Id, "detector", "1", new FaceBox(0, 0, 50, 50, 0.9f), Landmarks);
        var second = new Face(FaceId.New(), photo.Id, "detector", "1", new FaceBox(60, 0, 50, 50, 0.9f), Landmarks);
        await faces.BulkInsertAsync([first, second], default);

        await embeddings.BulkUpsertAsync(
            "embedder", "1", [new Application.Ports.FaceEmbedding(first.Id, new float[512])], default);

        await links.AddAsync(first.Id, second.Id, Application.Ports.FaceLinkKind.Cannot, default);

        await people.AddAsync(
            new Person(PersonId.New(), PersonName.Create("Alice"), DateTimeOffset.UnixEpoch), default);
    }

    [Fact]
    public async Task An_untouched_store_reports_nothing()
    {
        var contents = await Subject.DescribeAsync(default);

        contents.Photos.Should().Be(0);
        contents.Faces.Should().Be(0);
        contents.People.Should().Be(0);
    }

    [Fact]
    public async Task The_summary_reflects_what_is_stored()
    {
        await PopulateAsync();

        var contents = await Subject.DescribeAsync(default);

        contents.Photos.Should().Be(1);
        contents.Faces.Should().Be(2);
        contents.Embeddings.Should().Be(1);
        contents.People.Should().Be(1);
        contents.ScanRoots.Should().Be(1);
        contents.SizeOnDiskBytes.Should().BePositive();
    }

    [Fact]
    public async Task Clearing_empties_every_table()
    {
        await PopulateAsync();

        await Subject.ClearAllAsync(default);

        var contents = await Subject.DescribeAsync(default);
        contents.Should().BeEquivalentTo(
            new { Photos = 0, Faces = 0, Embeddings = 0, People = 0, Clusters = 0, ScanRoots = 0 },
            options => options.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Clearing_leaves_no_rows_behind_in_any_table()
    {
        // Checked directly against the schema rather than through the summary, so that a table
        // added later and forgotten by the reset is caught here rather than by a user whose
        // "empty" library still holds stale references.
        await PopulateAsync();

        await Subject.ClearAllAsync(default);

        await using var connection = _database.Connections.Open();
        await using var tables = connection.CreateCommand();
        tables.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> 'schema_version';";

        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                names.Add(reader.GetString(0));
            }
        }

        names.Should().NotBeEmpty("the schema should still exist after a reset");

        foreach (var name in names)
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {name};";
            Convert.ToInt32(await count.ExecuteScalarAsync(CancellationToken.None))
                .Should().Be(0, $"{name} should have been emptied by the reset");
        }
    }

    [Fact]
    public async Task The_schema_survives_so_the_app_keeps_working()
    {
        // Rows are deleted rather than the file, because the file is open and pooled while the
        // application runs. The store must be immediately usable afterwards, with no restart.
        await PopulateAsync();
        await Subject.ClearAllAsync(default);

        var photos = new SqlitePhotoRepository(_database.Connections);
        var photo = new Photo(PhotoId.New(), @"D:\photos\new.jpg", 10, DateTimeOffset.UnixEpoch);
        await photos.UpsertAsync(photo, default);

        (await photos.CountAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task Clearing_an_already_empty_store_is_harmless()
    {
        await Subject.ClearAllAsync(default);
        await Subject.ClearAllAsync(default);

        (await Subject.DescribeAsync(default)).Photos.Should().Be(0);
    }

    public void Dispose() => _database.Dispose();
}
