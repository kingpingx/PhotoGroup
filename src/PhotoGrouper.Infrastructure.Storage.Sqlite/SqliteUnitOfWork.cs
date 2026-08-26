using Microsoft.Data.Sqlite;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Infrastructure.Storage.Sqlite;

/// <summary>SQLite implementation of the transactional boundary.</summary>
public sealed class SqliteUnitOfWork(SqliteConnectionFactory connections) : IUnitOfWork
{
    public Task<ITransactionScope> BeginAsync(CancellationToken ct)
    {
        var connection = connections.Open();
        var transaction = connection.BeginTransaction();
        return Task.FromResult<ITransactionScope>(new Scope(connection, transaction));
    }

    private sealed class Scope(SqliteConnection connection, SqliteTransaction transaction) : ITransactionScope
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // Rolling back on an uncommitted dispose is what makes a failed export leave no
            // half-written journal behind: the caller does not have to remember to undo.
            if (!_committed)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
