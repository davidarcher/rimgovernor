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
    /// Version 32 drops the <c>Housing</c> phase latch from the rounds latches (#3659): the starter shelter is gone.
    /// Version 33 adds the <c>PollutionSupplies</c> cohort to the rounds review (#3317): the forbidden wastepacks the biotech/pollution Need may allow.
    /// Version 34 adds the step's <c>FullLoad</c> and <c>Collect</c> to journaled activity steps (#3046): a remote Supply's full carry.
    /// Version 35 adds <c>CancelsBlueprint</c> to the journaled deconstruction action (#3742): a retired room's blueprint is cancelled.
    /// Version 36 adds <c>FilthRuns</c> to the planned room (#3797): the station group's butchering load the room's cleanliness reads.
    /// Version 37 adds <c>Meals</c> to the planned room (#3802): the meals a day its stations make, which the kitchen credit prices.
    /// Version 38 reads mood evidence whole (#3768): a mood pawn's needs persist as has/lacks need levels, its flags and threshold as plain values, and a recovery worker's flags as plain booleans.
    /// Version 39 drops <c>Practice.Role</c>, <c>Practice.RequirementIndex</c> and <c>Practice.Beneficiary</c> from journaled activity steps (#3813): practice is every capable pawn's, no role mandate.
    /// Version 40 reads a pawn's Biotech block whole (#3773): the persisted PawnBiotech drops its unread life stage, learning, xenotype, hybrid, xenogerm and extractor facts and the gene's xenogene flag, and its deathrest keeps only deathresting and capacity.
    /// Version 41 reads recipe facts whole (#3873): the work-admission evidence's recipe input persists its slots and unfinished-item flag as plain values and adds its products and work amount.
    /// Version 42 reads a pawn's Biotech block whole (#3789): stage, genes, mechanitor, mech (work mode, energy, control group) and deathrest persist as plain values, absent records as null.
    /// Version 43 reads bill reservations whole (#3881): a journaled work admission's reservations persist as a plain list and each reservation's bill as a plain id.
    /// Version 44 reads disaster and recovery facts whole (#3888): a condition persists as permanent or timed, a short circuit as seen or none, and a recovery building's and candidate's flags as plain booleans with their hit points and fuel absent for an untracked or unrefuelable building.
    /// Version 45 reads the construction census whole (#3879): the work-admission evidence's frozen census is one StandingConstruction record, its standing buildings and owed material written together under the same Standing and Construction keys, or both null when no routine frame read it.
    /// Version 46 reads a combat role by duty (#3911): a role's post and standby cell are present or free, its fire is its own, a mortar crew (mortar, aim and shell together) or a ground shot, and its repair a door or none; the memory's sapper breach is guarded or not. A role writes Shell only for a mortar crew.
    /// Version 47 reads a colonist's mood whole (#3897): a mood persists as has (level and the forecast's target) or lacks, its thoughts as a plain list, and a mood method's need benefit as a plain number without the never-set mood benefit.
    /// Version 48 reads plan geometry whole (#3914): a planned room's door side is doored or doorless, a training range's facing and a planned site's rotation are toward a side or unturned, each absent from the record for the doorless or unturned state.
    /// Version 49 reads threat, disaster and recovery containers whole (#3931): a disaster history no longer persists WorkKnown, and a recovery record persists its safety (restrictions with a plain area id, empty for none) and buildings always, never null for an unread census.
    /// Version 50 drops the never-read <c>Observed</c> deficit fraction from a concern's progress and the <c>Alternative</c> text from a ready work (#3962).
    /// Version 51 reads a need target's claimant capacity and quantity whole (#3895): a persisted target capacity is its claimant count and, only for a stack, its units.
    /// Version 52 reads a building's interaction cell whole (#3957): a piece persists its InteractionOffset only when the building has one, and a piece read without the key has none.
    /// Version 53 reads the player's goals whole (#3949): a goal's deadline, theme and importance override persist as set or absent (the same null shape), the goal's unread site map and the preferences' unread wealth cap are gone, and the apparel and drug preferences always write a value (dressed and unmanaged for none set).
    /// Version 54 reads action progress and payloads whole (#3960): a building order persists its finishing-skill floor only when it names one, an owned-pawn setting is one arm per kind, a stockpile zone's setup is one record, the receipt and effect states are read by name (<c>Inconclusive</c> and <c>Unconfirmed</c> replace <c>Unknown</c>, <c>Unclassified</c> replaces an unknown refusal class).
    /// Version 55 reads a resource runway whole (#3926): a journaled runway persists its stock, surface ore and target as plain numbers and its rating as unrated, idle or burning (rate, stock days and days left together) with the deficit flag.
    /// Version 56 reads current work occupancy whole (#3896): the admission pawn row persists Occupied as a plain boolean.
    /// Version 57 reads medical history and pawn profiles whole (#3989): unresolved patients persist by identity, and work hours, inspiration and biotech effects/minimum ages are direct values.
    /// Version 58 reads admission evidence and recovery workers whole (#3892, #3982): material and production lifecycle states are explicit, and recovery worker rows are required.
    /// Version 59 retains canonical native activity locations, recovery and mood rows, and full pawn profiles with skill learning and explicit Biotech state (#3790). Existing session journals are invalidated; game saves are unchanged.
    /// </summary>
    public const int SchemaVersion = 59;

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
