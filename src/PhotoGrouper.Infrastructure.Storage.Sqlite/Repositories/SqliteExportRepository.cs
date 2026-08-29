using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Exporting;
using PhotoGrouper.Domain.Identity;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite.Repositories;

/// <summary>SQLite-backed storage for export runs and their operations.</summary>
public sealed class SqliteExportRepository(SqliteConnectionFactory connections) : IExportRepository
{
    private const string RunColumns =
        "id, started_utc, finished_utc, output_root, pattern, mode, source, status, undone_utc";

    private const string OpColumns =
        "id, run_id, photo_id, person_id, src_path, dst_path, op, status, bytes, error";

    public async Task AddRunAsync(ExportRun run, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO export_runs (id, started_utc, finished_utc, output_root, pattern, mode, source, status, undone_utc)
            VALUES ($id, $started, $finished, $root, $pattern, $mode, $source, $status, $undone);
            """;
        BindRun(command, run);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateRunAsync(ExportRun run, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE export_runs
            SET finished_utc = $finished, status = $status, undone_utc = $undone
            WHERE id = $id;
            """;
        BindRun(command, run);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ExportRun?> GetRunAsync(ExportRunId id, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM export_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(id.Value));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRun(reader) : null;
    }

    public async Task<IReadOnlyList<ExportRun>> GetRecentRunsAsync(int limit, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {RunColumns} FROM export_runs ORDER BY started_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ExportRun>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapRun(reader));
        }

        return results;
    }

    /// <remarks>
    /// One transaction for the whole plan. A half-written plan is worse than none: an undo reading
    /// it would put back the files it knew about and leave the rest where the interrupted run had
    /// moved them.
    /// </remarks>
    public async Task AddOpsAsync(IReadOnlyList<ExportOp> ops, CancellationToken ct)
    {
        if (ops.Count == 0)
        {
            return;
        }

        await using var connection = connections.Open();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO export_ops (id, run_id, photo_id, person_id, src_path, dst_path, op, status, bytes, error)
            VALUES ($id, $run, $photo, $person, $src, $dst, $op, $status, $bytes, $error);
            """;

        var parameters = CreateOpParameters(command);

        foreach (var op in ops)
        {
            ct.ThrowIfCancellationRequested();
            FillOp(parameters, op);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateOpAsync(ExportOp op, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE export_ops SET status = $status, bytes = $bytes, error = $error WHERE id = $id;";
        command.Parameters.AddWithValue("$status", (int)op.Status);
        command.Parameters.AddWithValue("$bytes", op.Bytes);
        command.Parameters.AddWithValue("$error", (object?)op.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(op.Id.Value));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportOp>> GetOpsAsync(ExportRunId runId, CancellationToken ct)
    {
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {OpColumns} FROM export_ops WHERE run_id = $run ORDER BY id;";
        command.Parameters.AddWithValue("$run", SqliteMappings.ToDb(runId.Value));

        var results = new List<ExportOp>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapOp(reader));
        }

        return results;
    }

    private static void BindRun(SqliteCommand command, ExportRun run)
    {
        command.Parameters.AddWithValue("$id", SqliteMappings.ToDb(run.Id.Value));
        command.Parameters.AddWithValue("$started", SqliteMappings.ToDb(run.StartedUtc));
        command.Parameters.AddWithValue(
            "$finished", run.FinishedUtc is { } f ? SqliteMappings.ToDb(f) : DBNull.Value);
        command.Parameters.AddWithValue("$root", run.OutputRoot);
        command.Parameters.AddWithValue("$pattern", run.Pattern);
        command.Parameters.AddWithValue("$mode", (int)run.Mode);
        command.Parameters.AddWithValue("$source", (int)run.Source);
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue(
            "$undone", run.UndoneUtc is { } u ? SqliteMappings.ToDb(u) : DBNull.Value);
    }

    private static SqliteParameter[] CreateOpParameters(SqliteCommand command)
    {
        string[] names = ["$id", "$run", "$photo", "$person", "$src", "$dst", "$op", "$status", "$bytes", "$error"];

        var parameters = new SqliteParameter[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            parameters[i] = command.Parameters.Add(new SqliteParameter(names[i], DBNull.Value));
        }

        return parameters;
    }

    private static void FillOp(SqliteParameter[] p, ExportOp op)
    {
        p[0].Value = SqliteMappings.ToDb(op.Id.Value);
        p[1].Value = SqliteMappings.ToDb(op.RunId.Value);
        p[2].Value = SqliteMappings.ToDb(op.PhotoId.Value);
        p[3].Value = op.PersonId is { } person ? SqliteMappings.ToDb(person.Value) : DBNull.Value;
        p[4].Value = op.SourcePath;
        p[5].Value = op.DestinationPath;
        p[6].Value = (int)op.Operation;
        p[7].Value = (int)op.Status;
        p[8].Value = op.Bytes;
        p[9].Value = (object?)op.Error ?? DBNull.Value;
    }

    private static ExportRun MapRun(SqliteDataReader reader) => new(
        new ExportRunId(reader.GetIdGuid(0)),
        reader.GetDateTimeOffset(1),
        reader.GetString(3),
        reader.GetString(4),
        (ExportMode)reader.GetInt32(5),
        (ExportSource)reader.GetInt32(6),
        (ExportRunStatus)reader.GetInt32(7),
        reader.GetNullableDateTimeOffset(2),
        reader.GetNullableDateTimeOffset(8));

    private static ExportOp MapOp(SqliteDataReader reader) => new(
        new ExportOpId(reader.GetIdGuid(0)),
        new ExportRunId(reader.GetIdGuid(1)),
        new PhotoId(reader.GetIdGuid(2)),
        reader.IsDBNull(3) ? null : new PersonId(reader.GetIdGuid(3)),
        reader.GetString(4),
        reader.GetString(5),
        (ExportMode)reader.GetInt32(6),
        (ExportOpStatus)reader.GetInt32(7),
        reader.GetInt64(8),
        reader.GetNullableString(9));
}
