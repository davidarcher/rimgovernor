using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;
using Thing = RimGovernor.Protocol.Observations.Thing;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>Pawns</c>: a frame's pawn table by id, every spawned pawn on the map, the canonical rows every other
/// section's pawn reference resolves against. A reference the table does not hold waits for the next frame: the
/// fact it feeds is unknown until then. The table is persistent: a version is immutable, so a consumer may keep it
/// across frames. The default value is the empty table.
/// </summary>
public readonly record struct Pawns(Table<PawnState> Table)
{
    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>Go's <c>NewPawns</c>: the table of rows by their pawn id.</summary>
    public static Pawns Of(params IEnumerable<PawnState> rows)
    {
        var table = default(Table<PawnState>);
        foreach (var row in rows)
        {
            table = table.Set(row.Pawn?.Id ?? "", row);
        }
        return new Pawns(table);
    }

    /// <summary>Go's <c>PawnsFromMap</c>: the table of rows, keyed as the map keys them.</summary>
    public static Pawns FromMap(IEnumerable<KeyValuePair<string, PawnState>> rows) => new(rows.ToTable());

    /// <summary>The table with <paramref name="row"/> added or replaced under <paramref name="id"/>.</summary>
    public Pawns With(string id, PawnState row) => new(Table.Set(id, row));

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Pawns Without(string id) => new(Table.Delete(id));

    /// <summary>The row of pawn <paramref name="id"/>, false when the table does not hold it.</summary>
    public bool TryGetValue(string id, out PawnState row) => Table.TryGetValue(id, out row!);

    /// <summary><paramref name="reference"/>'s canonical row, false when the table does not hold it.</summary>
    public bool TryRow(Ref reference, out PawnState row)
    {
        var ok = Table.TryGetValue(reference.Id, out row!);
        return ok && row is not null;
    }

    /// <summary>
    /// Go's <c>Snapshot</c>: the rows of <paramref name="ids"/> in id order as a list read answers them, null when the
    /// table misses one.
    /// </summary>
    public PawnSnapshot? Snapshot(ObservationContext context, IEnumerable<string> ids)
    {
        var snapshot = new PawnSnapshot { Context = context, Completeness = new Completeness() };
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            if (!TryRow(new Ref { Id = id }, out var row))
            {
                return null;
            }
            snapshot.Pawns.Add(row);
        }
        return snapshot;
    }
}

/// <summary>
/// Go's <c>Buildings</c>: a frame's building table by id, the canonical rows every other section's building
/// reference resolves against. A reference the table does not hold waits for the next frame.
/// </summary>
public readonly record struct Buildings(Table<BuildingState> Table)
{
    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>Go's <c>NewBuildings</c>: the table of rows by their building id; a row with no id is not addressable and is left out.</summary>
    public static Buildings Of(params IEnumerable<BuildingState> rows)
    {
        var table = default(Table<BuildingState>);
        foreach (var row in rows)
        {
            var id = row.Building?.Id ?? "";
            if (id.Length != 0)
            {
                table = table.Set(id, row);
            }
        }
        return new Buildings(table);
    }

    /// <summary>Go's <c>BuildingTable</c>: indexes the snapshot's rows by id; a null snapshot is an empty table.</summary>
    public static Buildings FromSnapshot(BuildingsSnapshot? snapshot) => snapshot is null ? default : Of(snapshot.Buildings);

    /// <summary>The table with <paramref name="row"/> added or replaced under <paramref name="id"/>.</summary>
    public Buildings With(string id, BuildingState row) => new(Table.Set(id, row));

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Buildings Without(string id) => new(Table.Delete(id));

    /// <summary>The row of building <paramref name="id"/>, false when the table does not hold it.</summary>
    public bool TryGetValue(string id, out BuildingState row) => Table.TryGetValue(id, out row!);

    /// <summary>The head of <paramref name="reference"/>'s row, null when the table does not hold it.</summary>
    public EntityRef? Entity(Ref? reference) =>
        reference is null ? null : Table.TryGetValue(reference.Id, out var row) ? row?.Building : null;

    /// <summary>
    /// <paramref name="reference"/>'s canonical row, false when the table does not hold it or the row's service or
    /// settings are malformed.
    /// </summary>
    public bool TryRow(Ref reference, out BuildingState row)
    {
        if (!Table.TryGetValue(reference.Id, out row!) || !ValidBuildingService(row.Service) || row.Settings is null)
        {
            row = null!;
            return false;
        }
        return true;
    }

    // Go's validBuildingService: a canonical row's service state: finite quantities, valid fuel definitions and
    // network id, and a "fuel" issue only where no fuel is reported.
    private static bool ValidBuildingService(BuildingServiceState? s)
    {
        if (s is null)
        {
            return false;
        }
        foreach (var value in new double?[] { s.HasFuel ? s.Fuel : null, s.HasTargetFuel ? s.TargetFuel : null })
        {
            if (value is { } v && (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 1e12))
            {
                return false;
            }
        }
        if (s.HasPowerOutputW && (double.IsNaN(s.PowerOutputW) || double.IsInfinity(s.PowerOutputW) || Math.Abs(s.PowerOutputW) > 1e12))
        {
            return false;
        }
        if (s.HasPowerNetId && (!BridgeFunctions.IsValidId(s.PowerNetId) || (s.HasConnected && !s.Connected)))
        {
            return false;
        }
        if (!BridgeFunctions.PawnsIssuesValid(s.Issues, s))
        {
            return false;
        }
        foreach (var issue in s.Issues)
        {
            if (issue.Field != "fuel"
                || issue.Unavailable?.Reason != UnavailableReason.NotApplicable
                || s.HasFuel
                || s.HasTargetFuel)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Go's <c>Things</c>: a frame's things table by id, the canonical rows every food stock reference resolves against.</summary>
public readonly record struct Things(Table<Thing> Table)
{
    /// <summary>Go's <c>ThingsSection</c>: names the things table in the colony mirror, keyed by thing id.</summary>
    public const string Section = "things";

    /// <summary>The number of rows.</summary>
    public int Count => Table.Count;

    /// <summary>Go's <c>NewThings</c>: the table of rows by their thing id.</summary>
    public static Things Of(params IEnumerable<Thing> rows)
    {
        var table = default(Table<Thing>);
        foreach (var row in rows)
        {
            table = table.Set(row.Thing_?.Id ?? "", row);
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
    public bool TryRow(Ref reference, out Thing row)
    {
        var ok = Table.TryGetValue(reference.Id, out row!);
        return ok && row is not null;
    }
}

/// <summary>
/// Go's <c>Tables</c>: a frame's keyed row tables, what every reference in its other sections resolves against.
/// </summary>
/// <param name="Buildings">The building table.</param>
/// <param name="Pawns">The pawn table.</param>
/// <param name="Things">The things table.</param>
/// <param name="FinishedResearch">
/// The finished research projects of the frame's research census; unknown where the frame carries none. Power rows
/// read it for the def's finished upgrades.
/// </param>
public sealed record Tables(Buildings Buildings, Pawns Pawns, Things Things, Fact<EquatableArray<string>> FinishedResearch = default)
{
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
            return thing.Thing_;
        }
        return null;
    }
}

/// <summary>
/// Go's <c>BuildingCensus</c>: a frame's player building table (the routine frame's construction census): the held
/// table version, the frame's context and the first invalid built row, which fails any reader of the construction
/// census.
/// </summary>
public sealed record BuildingCensus(ObservationContext? Context, Buildings Rows, BridgeContractException? Invalid = null, Memo? Memo = null);

/// <summary>
/// Go's <c>Memo</c>: holds one derived value a reader updates frame to frame: the update function gets the last
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
