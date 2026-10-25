using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Thing = RimGovernor.Brain.GameFacts.Observations.Thing;

namespace RimGovernor.Brain.GameAccess;

/// <summary>
/// A frame's pawn table by id, every spawned pawn on the map, the canonical rows every other
/// section's pawn reference resolves against. A reference the table does not hold waits for the next frame: the
/// fact it feeds is unknown until then. The table is persistent: a version is immutable, so a consumer may keep it
/// across frames. The default value is the empty table.
/// </summary>
public readonly record struct Pawns(Table<PawnState> Table)
{
    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>The table of rows by their pawn id.</summary>
    public static Pawns Of(params IEnumerable<PawnState> rows)
    {
        var table = default(Table<PawnState>);
        foreach (var row in rows)
        {
            table = table.Set(row.Pawn.Id, row);
        }
        return new Pawns(table);
    }

    /// <summary>The table with <paramref name="row"/> added or replaced under <paramref name="id"/>.</summary>
    public Pawns With(string id, PawnState row) => new(Table.Set(id, row));

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Pawns Without(string id) => new(Table.Delete(id));

    /// <summary>The row of pawn <paramref name="id"/>, false when the table does not hold it.</summary>
    public bool TryGetValue(string id, out PawnState row) => Table.TryGetValue(id, out row!);

    /// <summary><paramref name="reference"/>'s canonical row, false when the table does not hold it.</summary>
    public bool TryRow(Ref reference, out PawnState row)
    {
        var ok = Table.TryGetValue((reference.Id ?? ""), out row!);
        return ok && row is not null;
    }

    /// <summary>
    /// The rows of <paramref name="ids"/> in id order as a list read answers them, null when the
    /// table misses one.
    /// </summary>
    public PawnSnapshot? Snapshot(ObservationContext context, IEnumerable<string> ids)
    {
        var snapshot = new PawnSnapshot { Context = context, Pawns = [] };
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            if (!TryRow(new Ref { Id = id }, out var row))
            {
                return null;
            }
            snapshot = snapshot with { Pawns = [.. snapshot.Pawns, row] };
        }
        return snapshot;
    }
}

/// <summary>
/// A frame's building table by id, the canonical rows every other section's building
/// reference resolves against. A reference the table does not hold waits for the next frame.
/// </summary>
public readonly record struct Buildings(Table<BuildingState> Table)
{
    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>The table of rows by their building id.</summary>
    public static Buildings Of(params IEnumerable<BuildingState> rows)
    {
        var table = default(Table<BuildingState>);
        foreach (var row in rows)
        {
            table = table.Set(row.Building.Id, row);
        }
        return new Buildings(table);
    }

    /// <summary>The table with <paramref name="row"/> added or replaced under <paramref name="id"/>.</summary>
    public Buildings With(string id, BuildingState row) => new(Table.Set(id, row));

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Buildings Without(string id) => new(Table.Delete(id));

    /// <summary>The row of building <paramref name="id"/>, false when the table does not hold it.</summary>
    public bool TryGetValue(string id, out BuildingState row) => Table.TryGetValue(id, out row!);

    /// <summary>The head of <paramref name="reference"/>'s row, null when the table does not hold it.</summary>
    public EntityRef? Entity(Ref? reference) =>
        reference is null ? null : Table.TryGetValue(reference.Id ?? "", out var row) ? row.Building : null;

    /// <summary><paramref name="reference"/>'s canonical row, false when the table does not hold it.</summary>
    public bool TryRow(Ref reference, out BuildingState row) => Table.TryGetValue(reference.Id ?? "", out row!);
}

/// <summary>A frame's things table by id, the canonical rows every food stock reference resolves against.</summary>
public readonly record struct Things(Table<Thing> Table)
{
    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>The table of rows by their thing id.</summary>
    public static Things Of(params IEnumerable<Thing> rows)
    {
        var table = default(Table<Thing>);
        foreach (var row in rows)
        {
            table = table.Set(row.Entity.Id, row);
        }
        return new Things(table);
    }

    /// <summary>The table with <paramref name="row"/> added or replaced under <paramref name="id"/>.</summary>
    public Things With(string id, Thing row) => new(Table.Set(id, row));

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Things Without(string id) => new(Table.Delete(id));

    /// <summary>The row of thing <paramref name="id"/>, false when the table does not hold it.</summary>
    public bool TryGetValue(string id, out Thing row) => Table.TryGetValue(id, out row!);

    /// <summary><paramref name="reference"/>'s canonical row, false when the table does not hold it.</summary>
    public bool TryRow(Ref? reference, out Thing row)
    {
        var ok = Table.TryGetValue(reference?.Id ?? "", out row!);
        return ok && row is not null;
    }
}

/// <summary>
/// A frame's keyed row tables, what every reference in its other sections resolves against.
/// </summary>
/// <param name="Buildings">The building table.</param>
/// <param name="Pawns">The pawn table.</param>
/// <param name="Things">The things table.</param>
/// <param name="FinishedResearch">
/// The finished research projects of the frame's research census, which every frame carrying the tables holds. Power
/// rows read it for the def's finished upgrades.
/// </param>
public sealed record Tables(Buildings Buildings, Pawns Pawns, Things Things, EquatableArray<string> FinishedResearch)
{
    /// <summary>The complete source pawn census; absent only in non-colony table uses.</summary>
    public PawnSnapshot? PawnCensus { get; init; }

    /// <summary>The same-frame emergency census whose references the pawn table resolves.</summary>
    public RimGovernor.Brain.Policy.EmergencyFacts? Emergency { get; init; }
    /// <summary>The client's existing incremental construction projection; recordings derive directly from the rows.</summary>
    public Memo? ConstructionMemo { get; init; }

    /// <summary>The same-frame room census and its visible cell footprints.</summary>
    public RoomsSnapshot? Rooms { get; init; }

    /// <summary>The same-frame zone census, including growing fields used to price material arrivals.</summary>
    public ZonesSnapshot? Zones { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<RimGovernor.Brain.Domain.Cell>> RoomCells { get; init; } = new Dictionary<string, IReadOnlyList<RimGovernor.Brain.Domain.Cell>>();

    /// <summary>
    /// The head (def, label, position) of the row <paramref name="reference"/> points at in any of the tables, null
    /// when none holds it: the one place a reference reaches its def, label and position.
    /// </summary>
    public EntityRef? Entity(Ref? reference)
    {
        if (reference is null)
        {
            return null;
        }
        if (Buildings.Entity(reference) is { } head)
        {
            return head;
        }
        if (Pawns.TryRow(reference, out var pawn))
        {
            return pawn.Pawn;
        }
        if (Things.TryRow(reference, out var thing))
        {
            return thing.Entity;
        }
        return null;
    }
}

/// <summary>
/// A frame's player building table (the routine frame's construction census): the held
/// table version, the frame's context and the first invalid built row, which fails any reader of the construction
/// census.
/// </summary>
public sealed record BuildingCensus(ObservationContext? Context, Buildings Rows);

/// <summary>
/// Holds one derived value a reader updates frame to frame: the update function gets the last
/// value (null at first) and returns the next. Calls are serialised.
/// </summary>
public sealed class Memo
{
    private readonly object _gate = new();
    private object? _value;

    /// <summary>Replaces the memo's value with <c>update(last)</c> and returns it.</summary>
    public object? Update(Func<object?, object?> update)
    {
        lock (_gate)
        {
            _value = update(_value);
            return _value;
        }
    }
}
