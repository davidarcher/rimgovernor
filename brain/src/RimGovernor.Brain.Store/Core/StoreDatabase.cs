using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The controller's SQLite journal file (Go <c>store.Store</c>'s connection handling): WAL, foreign keys, one
/// connection serialised by a gate (Go <c>SetMaxOpenConns(1)</c>), and every transaction opened
/// <c>BEGIN IMMEDIATE</c> and retried while SQLite reports busy or locked.
/// </summary>
public sealed class StoreDatabase : IAsyncDisposable, IDisposable
{
    /// <summary>The <c>PRAGMA user_version</c> <see cref="OpenAsync"/> requires; a database from another version is refused.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The <c>PRAGMA application_id</c> ("RGO1").</summary>
    public const int ApplicationId = 0x52474f31;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;
    private static readonly TimeSpan _busyRetry = TimeSpan.FromMilliseconds(10);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StoreDatabase(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    /// <summary>The filesystem path the database was opened at.</summary>
    public string Path { get; }

    /// <summary>
    /// Opens (creating it when new) the journal at a filesystem path, never a caller-supplied connection string.
    /// Throws <see cref="IncompatibleDatabaseException"/> for a database another schema version or application wrote.
    /// </summary>
    public static async Task<StoreDatabase> OpenAsync(string path, CancellationToken cancellationToken)
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
        var db = new StoreDatabase(connection, full);
        try
        {
            await RetryBusyAsync(
                    async () =>
                    {
                        if (connection.State != System.Data.ConnectionState.Open)
                        {
                            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                        }
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            // WAL is persistent in the file, so it is switched once here, after the per-connection synchronous
            // setting applies, rather than on every open.
            await db.ExecutePragmaAsync("PRAGMA synchronous=NORMAL", cancellationToken).ConfigureAwait(false);
            var journal = await RetryBusyAsync(
                    async () => Convert.ToString(await db.ScalarOnConnectionAsync("PRAGMA journal_mode=WAL", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"journal mode \"{journal}\", want wal");
            }
            await db.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// <see cref="OpenAsync"/> for the service: a database from another schema version is not migrated (saves
    /// regenerate) but moved aside, with its WAL and shared-memory files, to <c>&lt;path&gt;.v&lt;old&gt;.bak[.n]</c>,
    /// and a fresh one is created in its place. <c>MovedAside</c> is that path, or null when the database opened as it was.
    /// </summary>
    public static async Task<(StoreDatabase Database, string? MovedAside)> OpenOrReplaceAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (await OpenAsync(path, cancellationToken).ConfigureAwait(false), null);
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
            return (await OpenAsync(path, cancellationToken).ConfigureAwait(false), aside);
        }
    }

    /// <summary>
    /// Copies the database into a new file at <paramref name="destination"/> through SQLite's online backup,
    /// consistent while the journal stays open. The destination must not exist.
    /// </summary>
    public async Task SnapshotAsync(string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            throw new IOException($"snapshot: {destination} already exists");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = System.IO.Path.GetFullPath(destination), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()
            );
            await using (target.ConfigureAwait(false))
            {
                await target.OpenAsync(cancellationToken).ConfigureAwait(false);
                _connection.BackupDatabase(target);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The persistent controller identity. Never creates or repairs metadata: missing or corrupt identity must
    /// not turn an uncertain old attempt into a new execution namespace.
    /// </summary>
    public async Task<ControllerSessionId> IdentityAsync(CancellationToken cancellationToken)
    {
        var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            var id = await IdentityAsync(tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
    }

    /// <summary>Go <c>core.Identity</c>.</summary>
    internal static async Task<ControllerSessionId> IdentityAsync(StoreTransaction tx, CancellationToken cancellationToken)
    {
        var rows = await tx.QueryAsync("SELECT singleton,controller_session_id FROM metadata", [], r => (r.GetInt64(0), r.GetString(1)), cancellationToken).ConfigureAwait(false);
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
    public async Task<StoreTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RetryBusyAsync(() => ExecuteOnConnectionAsync("BEGIN IMMEDIATE", cancellationToken), cancellationToken).ConfigureAwait(false);
            return new StoreTransaction(_connection, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private static readonly object _providerLock = new();
    private static bool _providerReady;

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

    /// <summary>Maps a SQLite constraint violation to <see cref="ConflictException"/> (Go <c>core.Conflict</c>).</summary>
    public static Exception Conflict(Exception error) =>
        error is SqliteException sqlite && (sqlite.SqliteErrorCode & 255) == SqliteConstraint ? new ConflictException(error) : error;

    /// <summary>Repeats <paramref name="operation"/> while it fails with SQLITE_BUSY or SQLITE_LOCKED.</summary>
    internal static async Task RetryBusyAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        _ = await RetryBusyAsync(
                async () =>
                {
                    await operation().ConfigureAwait(false);
                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal static async Task<T> RetryBusyAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException e) when ((e.SqliteErrorCode & 255) is SqliteBusy or SqliteLocked)
            {
                await Task.Delay(_busyRetry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            var version = await tx.ScalarLongAsync("PRAGMA user_version", cancellationToken).ConfigureAwait(false);
            var app = await tx.ScalarLongAsync("PRAGMA application_id", cancellationToken).ConfigureAwait(false);
            if (version == 0 && app == 0)
            {
                if (await tx.ScalarLongAsync("SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'", cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidOperationException("unversioned nonempty database is incompatible");
                }
                await tx.ExecuteAsync("CREATE TABLE metadata(singleton INTEGER PRIMARY KEY CHECK(singleton=1), controller_session_id TEXT NOT NULL) STRICT", cancellationToken).ConfigureAwait(false);
                await tx.ExecuteAsync(
                        "INSERT INTO metadata(singleton,controller_session_id) VALUES(1,$id)",
                        cancellationToken,
                        ("$id", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
                    )
                    .ConfigureAwait(false);
                await tx.ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version={SchemaVersion}"), cancellationToken).ConfigureAwait(false);
                await tx.ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={ApplicationId}"), cancellationToken).ConfigureAwait(false);
            }
            else if (version != SchemaVersion || app != ApplicationId)
            {
                throw new IncompatibleDatabaseException(string.Create(CultureInfo.InvariantCulture, $"{app}/{version}"));
            }
            _ = await IdentityAsync(tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecutePragmaAsync(string sql, CancellationToken cancellationToken) =>
        await ExecuteOnConnectionAsync(sql, cancellationToken).ConfigureAwait(false);

    private async Task ExecuteOnConnectionAsync(string sql, CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<object?> ScalarOnConnectionAsync(string sql, CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
