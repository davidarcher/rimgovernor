namespace RimGovernor.Brain.Facts;

// Keyed tables: per section, the keyed rows of the last whole frame the native answered and the tick they
// describe, held in the FactStore beside the decoded sections and emptied with them on a scope change. Tables are
// immutable once published, and every table goes to the store's Recorder, which turns successive tables into
// a recording.

/// <summary>The tick a table's rows describe.</summary>
public readonly record struct Watermark(long Tick)
{
    /// <summary>The watermark of a read at <paramref name="tick"/>.</summary>
    public static Watermark At(long tick) => new(tick);
}

/// <summary>One keyed section as the store holds it: Rows as of AsOf, published at table Version. Never mutated once published.</summary>
public sealed record Table<TKey, TRow>(IReadOnlyDictionary<TKey, TRow> Rows, Watermark AsOf, ulong Version)
    where TKey : notnull;

/// <summary>
/// A section's rows as a persistent table version: the recorder reads what changed since the previous version
/// without walking the rows both versions share. The bridge Table implements it.
/// </summary>
public interface IKeyedRows
{
    /// <summary>Calls <paramref name="row"/> for every row of the table: its id and the row.</summary>
    void Rows(Action<string, object> row);

    /// <summary>
    /// Calls <paramref name="changed"/> for each row new or different since <paramref name="previous"/> (another
    /// table of the same type, or null) and <paramref name="removed"/> for each id previous held that the table lacks.
    /// </summary>
    void ChangedSince(object? previous, Action<string, object> changed, Action<string> removed);
}

/// <summary>A section published as a persistent table version.</summary>
public sealed record KeyedTable(IKeyedRows Rows, Watermark AsOf, ulong Version);

/// <summary>
/// One table as the store published it, handed to a Recorder: the section's whole rows (an
/// <c>IReadOnlyDictionary&lt;K, R&gt;</c>, or an <see cref="IKeyedRows"/>) at Version, complete through AsOf,
/// under Scope. Rows is never mutated after publishing, so a recorder may keep it.
/// </summary>
public sealed record Published(Scope Scope, string Section, ulong Version, Watermark AsOf, object Rows);

/// <summary>
/// Sees every table the store publishes, in publish order, under the store's lock: a recording
/// (MirrorRecorder) turns them into keyframes and the rows that changed between tables. It must not call back
/// into the store.
/// </summary>
public delegate void Recorder(Published published);

public static class StoreTables
{
    /// <summary>The section's table under scope, false when none is held.</summary>
    public static bool TryGetTable<TKey, TRow>(FactStore? store, Scope scope, string name, out Table<TKey, TRow> table)
        where TKey : notnull
    {
        table = default!;
        if (store is null)
        {
            return false;
        }
        lock (store.Gate)
        {
            if (store.CurrentScope != scope || !store.Tables.TryGetValue(name, out var held) || held is not Table<TKey, TRow> typed)
            {
                return false;
            }
            table = typed;
            return true;
        }
    }

    /// <summary>
    /// Publishes rows, a whole frame's section, as the section's table under scope, emptying the store first
    /// when scope differs from the one its sections belong to.
    /// </summary>
    public static Table<TKey, TRow> PutTable<TKey, TRow>(FactStore? store, Scope scope, string name, IReadOnlyDictionary<TKey, TRow>? rows, Watermark asOf)
        where TKey : notnull
    {
        rows ??= new Dictionary<TKey, TRow>();
        if (store is null)
        {
            return new Table<TKey, TRow>(rows, asOf, 0);
        }
        lock (store.Gate)
        {
            store.Rescope(scope);
            var table = new Table<TKey, TRow>(rows, asOf, store.NextTableVersion());
            store.Tables[name] = table;
            store.CurrentRecorder?.Invoke(new Published(scope, name, table.Version, asOf, rows));
            return table;
        }
    }

    /// <summary>
    /// Publishes a persistent table version as the section's table under scope (PutTable's counterpart for the
    /// big keyed sections): the store keeps the version itself, with no per-publish copy, and hands it to the
    /// Recorder, which diffs it against the last it saw.
    /// </summary>
    public static ulong PutKeyed(FactStore? store, Scope scope, string name, IKeyedRows rows, Watermark asOf)
    {
        if (store is null)
        {
            return 0;
        }
        lock (store.Gate)
        {
            store.Rescope(scope);
            var table = new KeyedTable(rows, asOf, store.NextTableVersion());
            store.Tables[name] = table;
            store.CurrentRecorder?.Invoke(new Published(scope, name, table.Version, asOf, rows));
            return table.Version;
        }
    }

    /// <summary>The section's persistent table under scope, false when none is held.</summary>
    public static bool TryGetKeyed(FactStore? store, Scope scope, string name, out KeyedTable table)
    {
        table = default!;
        if (store is null)
        {
            return false;
        }
        lock (store.Gate)
        {
            if (store.CurrentScope != scope || !store.Tables.TryGetValue(name, out var held) || held is not KeyedTable typed)
            {
                return false;
            }
            table = typed;
            return true;
        }
    }
}
