using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The controller's SQLite journal file: WAL, foreign keys, one connection serialised by
/// a gate, and every transaction opened <c>BEGIN IMMEDIATE</c> and retried while
/// SQLite reports busy or locked. The behaviour lives in partial files beside this one, one per concern.
/// </summary>
public sealed partial class Store : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The <c>PRAGMA user_version</c> <see cref="Open"/> requires; a database from another version is not
    /// migrated (see <see cref="OpenOrReplace"/>). Version 13 drops the clock attempt, epoch, receipt and retirement tables: JSON payloads written by the
    /// hand-written codecs, with no per-kind column checks; the Standard payload gained the planner's Record text.
    /// Version 14 adds the Need owner (#3250): <c>plan_owner</c> kind <c>need</c> and the <c>need_methods</c> table.
    /// Version 15 adds the <c>admin_creep_joiners</c> family record (#3264).
    /// Version 16 adds the ideology family's pending reform (#3260): the <c>ideology_reform</c> singleton table.
    /// Version 17 adds the economy family's trade missions (#3316): the <c>economy_trade_missions</c> singleton table.
    /// Version 18 drops the routes latch from the stored review (#3265): routes is the layout/routes Need.
    /// Version 19 drops Standards and incidents (#3265): the <c>standards</c>, <c>incidents</c>, <c>standard_methods</c>
    /// and <c>incident_methods</c> tables and the <c>plan_methods</c> view; <c>plan_owner</c> holds Needs only.
    /// Version 20 renames <c>concern_transitions</c> to <c>need_transitions</c>, keyed by the <c>need</c> column (#3265).
    /// Version 21 drops the <c>combat_evidence</c> table (#3407): its only reader was an acceptance case; OTLP keeps the stop history.
    /// Version 22 drops the seven <c>clock_*</c> journal tables (#3417): the brain keeps no clock events; the native ring is read live.
    /// Version 24 adds the <c>goals</c> singleton table, the session cache of the <c>family/goals</c> record (#3489).
    /// Version 25 drops the <c>ledger_rows</c> table (#3566): the derived ledger copy every review rewrote had no reader.
    /// Version 26 is the pawn-activities action body with the step's <c>Targets</c> and <c>Cells</c> (#3613), the task's
    /// <c>PriorSteps</c> (#3617) and <c>UseUntilTick</c> (#3167): a version-25 plan row no longer decodes.
    /// Version 27 adds the practice surgery variant to journaled activity steps (#3637): <c>Practice.Surgery</c> (recipe, part, medicine).
    /// Version 28 drops <c>Surgeon</c> from the journaled surgery action (#3638): peg-leg doctor training, its only writer, is gone.
    /// Version 29 drops <c>Practice.Surgery</c> from journaled activity steps (#3344): practice surgery is gone.
    /// Version 30 drops the <c>Clean</c> action and <c>WaitForFilth</c> from journaled activity steps (#3667): coupled cleaning is gone.
    /// Version 31 drops the <c>Standby</c> activity kind from journaled activity steps (#3669): an unowned colonist runs vanilla work.
    /// </summary>
    public const int SchemaVersion = 31;

    /// <summary>The <c>PRAGMA application_id</c> ("RGO1").</summary>
    public const int ApplicationId = 0x52474f31;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;
    private static readonly TimeSpan _busyRetry = TimeSpan.FromMilliseconds(10);
    private static readonly object _providerLock = new();
    private static bool _providerReady;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PlanCache _plans = new();
    private readonly RoundsSlot _rounds = new();

    private Store(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
        Floors = RetirementFloors.For(path);
    }

    /// <summary>The filesystem path the database was opened at.</summary>
    public string Path { get; }

    internal RetirementFloors Floors { get; }

    /// <summary>How often a plan read was served from the cache.</summary>
    public PlanCacheStats PlanCacheStats() => _plans.Stats;

    /// <summary>
    /// Opens (creating it when new) the journal at a filesystem path, never a caller-supplied connection string.
    /// Throws <see cref="IncompatibleDatabaseException"/> for a database another schema version or application wrote.
    /// </summary>
    public static Store Open(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("database path is empty", nameof(path));
        }
        EnsureProvider();
        var full = System.IO.Path.GetFullPath(path);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 1,
            }.ToString()
        );
        var db = new Store(connection, full);
        try
        {
            RetryBusy(
                () =>
                {
                    if (connection.State != System.Data.ConnectionState.Open)
                    {
                        connection.Open();
                    }
                    return true;
                },
                cancellationToken
            );
            // WAL is persistent in the file, so it is switched once here, after the per-connection synchronous
            // setting applies, rather than on every open.
            _ = db.OnConnection("PRAGMA synchronous=NORMAL");
            var journal = RetryBusy(() => Convert.ToString(db.OnConnection("PRAGMA journal_mode=WAL"), CultureInfo.InvariantCulture), cancellationToken);
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"journal mode \"{journal}\", want wal");
            }
            db.Initialize(cancellationToken);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary><see cref="Open"/> as a task.</summary>
    public static async Task<Store> OpenAsync(string path, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return Open(path, cancellationToken);
    }

    /// <summary>
    /// <see cref="Open"/> for the service: a database from another schema version is not migrated (saves
    /// regenerate) but moved aside, with its WAL and shared-memory files, to <c>&lt;path&gt;.v&lt;old&gt;.bak[.n]</c>,
    /// and a fresh one is created in its place. <c>MovedAside</c> is that path, or null when the database opened as it was.
    /// </summary>
    public static (Store Journal, string? MovedAside) OpenOrReplace(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (Open(path, cancellationToken), null);
        }
        catch (IncompatibleDatabaseException incompatible)
        {
            var old = path + ".v" + incompatible.Found.Replace('/', '-') + ".bak";
            var aside = old;
            for (var n = 1; File.Exists(aside); n++)
            {
                aside = string.Create(CultureInfo.InvariantCulture, $"{old}.{n}");
            }
            File.Move(path, aside);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, aside + suffix);
                }
            }
            return (Open(path, cancellationToken), aside);
        }
    }

    /// <summary><see cref="OpenOrReplace"/> as a task.</summary>
    public static async Task<(Store Journal, string? MovedAside)> OpenOrReplaceAsync(string path, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return OpenOrReplace(path, cancellationToken);
    }

    /// <summary>
    /// Copies the database into a new file at <paramref name="destination"/> through SQLite's online backup,
    /// consistent while the journal stays open. The destination must not exist.
    /// </summary>
    public void Snapshot(string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            throw new IOException($"snapshot: {destination} already exists");
        }
        _gate.Wait(cancellationToken);
        try
        {
            using var target = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = System.IO.Path.GetFullPath(destination), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()
            );
            target.Open();
            _connection.BackupDatabase(target);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary><see cref="Snapshot"/> as a task.</summary>
    public async Task SnapshotAsync(string destination, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        Snapshot(destination, cancellationToken);
    }

    /// <summary>
    /// The persistent controller identity. Never creates or repairs metadata: missing or corrupt identity must
    /// not turn an uncertain old attempt into a new execution namespace.
    /// </summary>
    public ControllerSessionId Identity(CancellationToken cancellationToken) => Run(IdentityIn, cancellationToken);

    /// <summary><see cref="Identity"/> as a task.</summary>
    public Task<ControllerSessionId> IdentityAsync(CancellationToken cancellationToken) => RunAsync(IdentityIn, cancellationToken);

    internal static ControllerSessionId IdentityIn(StoreTransaction tx)
    {
        var rows = tx.Query("SELECT singleton,controller_session_id FROM metadata", [], r => (r.GetInt64(0), r.GetString(1)));
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("controller identity metadata is missing");
        }
        if (rows.Count > 1)
        {
            throw new InvalidOperationException("controller identity metadata has multiple rows");
        }
        var (singleton, id) = rows[0];
        var valid = singleton == 1 && id.Length == 64;
        if (valid)
        {
            try
            {
                valid = string.Equals(Convert.ToHexStringLower(Convert.FromHexString(id)), id, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                valid = false;
            }
        }
        return valid ? new ControllerSessionId(id) : throw new InvalidOperationException("corrupt controller identity metadata");
    }

    /// <summary>Opens a transaction: waits for the connection gate, then <c>BEGIN IMMEDIATE</c>, retried while busy.</summary>
    internal StoreTransaction Begin(CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            RetryBusy(() => OnConnection("BEGIN IMMEDIATE"), cancellationToken);
            return new StoreTransaction(_connection, _gate, _plans, _rounds, Floors, cancellationToken);
        }
        catch
        {
            _ = _gate.Release();
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction and commits it; an exception rolls it back.</summary>
    internal T Run<T>(Func<StoreTransaction, T> work, CancellationToken cancellationToken)
    {
        using var tx = Begin(cancellationToken);
        var result = work(tx);
        tx.Commit();
        return result;
    }

    internal void Run(Action<StoreTransaction> work, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                work(tx);
                return true;
            },
            cancellationToken
        );

    /// <summary><see cref="Run{T}"/> as a task; a failure surfaces when the task is awaited.</summary>
    internal async Task<T> RunAsync<T>(Func<StoreTransaction, T> work, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return Run(work, cancellationToken);
    }

    internal async Task RunAsync(Action<StoreTransaction> work, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        Run(work, cancellationToken);
    }

    /// <summary>Maps a SQLite constraint violation to <see cref="ConflictException"/>.</summary>
    public static Exception Conflict(Exception error) =>
        error is SqliteException sqlite && (sqlite.SqliteErrorCode & 255) == SqliteConstraint ? new ConflictException(error) : error;

    /// <summary>Repeats <paramref name="operation"/> while it fails with SQLITE_BUSY or SQLITE_LOCKED.</summary>
    internal static T RetryBusy<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (SqliteException e) when ((e.SqliteErrorCode & 255) is SqliteBusy or SqliteLocked)
            {
                if (cancellationToken.WaitHandle.WaitOne(_busyRetry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Registers the native SQLite provider once. The <c>SourceGear.sqlite3</c> library (named e_sqlite3) is
    /// used instead of the Microsoft.Data.Sqlite bundle, whose pinned native build carries a known advisory.
    /// </summary>
    private static void EnsureProvider()
    {
        lock (_providerLock)
        {
            if (_providerReady)
            {
                return;
            }
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            _providerReady = true;
        }
    }

    private void Initialize(CancellationToken cancellationToken)
    {
        using var tx = Begin(cancellationToken);
        var version = tx.Scalar("PRAGMA user_version");
        var app = tx.Scalar("PRAGMA application_id");
        if (version == 0 && app == 0)
        {
            if (tx.Scalar("SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'") != 0)
            {
                throw new InvalidOperationException("unversioned nonempty database is incompatible");
            }
            StoreSchema.Create(tx);
            _ = tx.Execute("INSERT INTO metadata(singleton,controller_session_id) VALUES(1,$id)", ("$id", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
            _ = tx.Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version={SchemaVersion}"));
            _ = tx.Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={ApplicationId}"));
        }
        else if (version != SchemaVersion || app != ApplicationId)
        {
            throw new IncompatibleDatabaseException(string.Create(CultureInfo.InvariantCulture, $"{app}/{version}"));
        }
        // A matching version marker alone does not establish the expected tables.
        StoreSchema.Check(tx);
        _ = IdentityIn(tx);
        InitializeChecks(tx);
        tx.Commit();
    }

    private object? OnConnection(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
