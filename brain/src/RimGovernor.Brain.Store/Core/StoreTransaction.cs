using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RimGovernor.Brain.Store;

/// <summary>
/// An open <c>BEGIN IMMEDIATE</c> transaction holding the store's connection gate. Disposing without
/// <see cref="CommitAsync"/> rolls back (Go's <c>defer tx.Rollback()</c>).
/// </summary>
public sealed class StoreTransaction : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate;
    private bool _finished;

    internal StoreTransaction(SqliteConnection connection, SemaphoreSlim gate)
    {
        _connection = connection;
        _gate = gate;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("COMMIT", cancellationToken).ConfigureAwait(false);
        Finish();
    }

    /// <summary>Runs a statement with named parameters (<c>$name</c>); returns the rows affected.</summary>
    public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var command = Prepare(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first column of the first row as an integer.</summary>
    public async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var command = Prepare(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Reads every row of a query through <paramref name="map"/>.</summary>
    public async Task<List<T>> QueryAsync<T>(string sql, (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> map, CancellationToken cancellationToken)
    {
        var command = Prepare(sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<T>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(map(reader));
                }
                return rows;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_finished)
        {
            return;
        }
        try
        {
            // Rollback never observes the caller's cancellation: the gate must be released with a clean connection.
            await ExecuteAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The transaction already ended (for example, a failed COMMIT); nothing is left to undo.
        }
        finally
        {
            Finish();
        }
    }

    private SqliteCommand Prepare(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        _ = _gate.Release();
    }
}
