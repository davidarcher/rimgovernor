using System.Globalization;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// An open <c>BEGIN IMMEDIATE</c> transaction holding the store's connection gate (Go <c>Tx</c>). Disposing without
/// <see cref="Commit"/> rolls back (Go's <c>defer tx.Rollback()</c>). SQLite has no real asynchrony, so every
/// statement here runs synchronously and observes the cancellation token of the call that opened it.
/// </summary>
public sealed class StoreTransaction : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate;
    private bool _finished;

    internal StoreTransaction(SqliteConnection connection, SemaphoreSlim gate, PlanCache plans, RetirementFloors floors, CancellationToken cancellationToken)
    {
        _connection = connection;
        _gate = gate;
        Plans = plans;
        Floors = floors;
        Cancellation = cancellationToken;
    }

    /// <summary>The decoded-plan cache; a plan read goes through <see cref="Store.LoadIn"/>, never around it.</summary>
    internal PlanCache Plans { get; }

    /// <summary>The retirement floors shared by every handle on this database path.</summary>
    internal RetirementFloors Floors { get; }

    /// <summary>The token of the call that opened the transaction; each statement observes it.</summary>
    public CancellationToken Cancellation { get; }

    public void Commit()
    {
        RunStatement("COMMIT", []);
        Plans.Committed();
        Finish();
    }

    /// <summary>Runs a statement with named parameters (<c>$name</c>); returns the rows affected.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        Plans.Observe(sql);
        return RunStatement(sql, parameters);
    }

    /// <summary>
    /// <see cref="Execute"/> for a write that changes only <paramref name="plan"/>'s cached state, so the other
    /// cached plans survive it.
    /// </summary>
    internal int ExecuteForPlan(PlanId plan, string sql, params (string Name, object? Value)[] parameters)
    {
        Plans.Observe(plan);
        return RunStatement(sql, parameters);
    }

    /// <summary>
    /// <see cref="ExecuteForPlan"/> for an insert that may collide with an existing identity: a constraint
    /// violation surfaces as <see cref="ConflictException"/> (Go <c>conflict(err)</c>).
    /// </summary>
    internal int ExecuteConflict(PlanId plan, string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            return ExecuteForPlan(plan, sql, parameters);
        }
        catch (SqliteException e) when (Store.Conflict(e) is ConflictException conflict)
        {
            throw conflict;
        }
    }

    /// <summary><see cref="Execute"/> with a constraint violation surfacing as <see cref="ConflictException"/>.</summary>
    internal int ExecuteOrConflict(string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            return Execute(sql, parameters);
        }
        catch (SqliteException e) when (Store.Conflict(e) is ConflictException conflict)
        {
            throw conflict;
        }
    }

    /// <summary>The first column of the first row as an integer.</summary>
    public long Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The first column of the first row as text, or null when there is no row or the value is NULL.</summary>
    public string? ScalarText(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        return command.ExecuteScalar() is { } value and not DBNull ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    public bool Exists(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    /// <summary>Reads every row of a query through <paramref name="map"/>.</summary>
    public List<T> Query<T>(string sql, (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> map)
    {
        using var command = Prepare(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    /// <summary>The first row of a query, or <c>default</c> (with <c>found</c> false) when it has none.</summary>
    public (T? Row, bool Found) QueryFirst<T>(string sql, (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> map)
    {
        using var command = Prepare(sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (map(reader), true) : (default, false);
    }

    /// <summary>Opens a savepoint, so a rejected item rolls back only its own writes.</summary>
    internal void Savepoint(string name) => RunStatement("SAVEPOINT " + name, []);

    internal void ReleaseSavepoint(string name) => RunStatement("RELEASE " + name, []);

    /// <summary>Rolls back to the savepoint; plan rows read since it may now be stale, so the cache is dropped.</summary>
    internal void RollbackToSavepoint(string name)
    {
        RunStatement("ROLLBACK TO " + name, []);
        Plans.RolledBack();
    }

    /// <summary>SQLite's data version, which changes when another connection commits.</summary>
    internal long DataVersion() => Scalar("PRAGMA data_version");

    public void Dispose()
    {
        if (_finished)
        {
            return;
        }
        try
        {
            // Rollback never observes the caller's cancellation: the gate must be released with a clean connection.
            using var command = _connection.CreateCommand();
            command.CommandText = "ROLLBACK";
            _ = command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The transaction already ended (for example, a failed COMMIT); nothing is left to undo.
        }
        finally
        {
            Plans.RolledBack();
            Finish();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private int RunStatement(string sql, (string Name, object? Value)[] parameters)
    {
        using var command = Prepare(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, (string Name, object? Value)[] parameters)
    {
        Cancellation.ThrowIfCancellationRequested();
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, Sql(value));
        }
        return command;
    }

    /// <summary>The database value of a parameter: identifiers unwrap to their text or number, the rest pass through.</summary>
    private static object Sql(object? value) =>
        value switch
        {
            null => DBNull.Value,
            ColonyId v => v.Value,
            LoadId v => v.Value,
            ActionId v => v.Value,
            PlanId v => v.Value,
            IncidentId v => v.Value,
            PawnId v => v.Value,
            QuestId v => v.Value,
            ConcernId v => v.Value,
            StandardId v => v.Value,
            MethodId v => v.Value,
            MapId v => v.Value,
            Tick v => v.Value,
            AttemptId v => v.Value,
            PlanRevision v => v.Value.ToString(CultureInfo.InvariantCulture),
            NativeGeneration v => v.Value.ToString(CultureInfo.InvariantCulture),
            ulong v => v.ToString(CultureInfo.InvariantCulture),
            _ => value,
        };

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

/// <summary>Column readers shared by the store's row mappers.</summary>
internal static class Rows
{
    public static string? Text(this SqliteDataReader r, int column) => r.IsDBNull(column) ? null : r.GetString(column);

    public static long? Number(this SqliteDataReader r, int column) => r.IsDBNull(column) ? null : r.GetInt64(column);

    public static ulong UnsignedText(this SqliteDataReader r, int column)
    {
        var text = r.GetString(column);
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n.ToString(CultureInfo.InvariantCulture) == text
            ? n
            : throw new InvalidOperationException("corrupt integer text");
    }
}
