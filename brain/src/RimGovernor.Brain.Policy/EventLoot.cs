namespace RimGovernor.Brain.Policy;

public sealed record LootItem
{
    public StartingSupply Supply { get; init; } = new();

    public bool Forbidden { get; init; }

    /// <summary>The colonist route verdict: safe with its measured path, exposed to a hazard, or reached by no colonist.</summary>
    public RimGovernor.Contracts.Wire.Observations.HaulSafety Haul { get; init; } = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Unreached();

    /// <summary>Whether some colonist has a hazard-free route to the stack and back.</summary>
    public bool IsSafe => Haul is RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe;

    /// <summary>Whether colonists reach the stack but every route meets a hazard.</summary>
    public bool IsExposed => Haul is RimGovernor.Contracts.Wire.Observations.HaulSafety.Unsafe;

    /// <summary>Whether no colonist reaches the stack, so its safety is not measured.</summary>
    public bool IsUnreached => Haul is RimGovernor.Contracts.Wire.Observations.HaulSafety.Unreached;

    /// <summary>The shortest safe route in cells; false unless the stack is safe to haul.</summary>
    public bool SafePath(out double pathLength)
    {
        pathLength = Haul is RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength: var path } ? path : 0;
        return IsSafe;
    }

    /// <summary>
    /// Marks a def a native spawner makes forbidden on purpose (a hive's insect jelly:
    /// CompProperties_Spawner.spawnForbidden in the def mirror); such a stack is never released
    /// (<c>FilterLootRelease</c>).
    /// </summary>
    public bool SpawnForbidden { get; init; }

    /// <summary>The stack size; feeds remote loot's reach and demand filter.</summary>
    public long Count { get; init; }

    /// <summary>The item units the player's accepting storage can still take for the stack's def.</summary>
    public long StorageHeadroom { get; init; }

    /// <summary>
    /// The storage groups (owned zones and storage buildings, by id) whose filter accepts the stack, ignoring
    /// space. Native fills it only on an unforbidden row with zero headroom that is not exposed
    /// (<see cref="StoreRules.StrandedStacks"/>); empty elsewhere.
    /// </summary>
    public EquatableArray<string> AcceptedBy { get; init; }

    /// <summary>The stack's deterioration where it lies (#3798); null when it cannot deteriorate.</summary>
    public RimGovernor.Contracts.Wire.Observations.LootDeterioration? Deterioration { get; init; }
}

/// <summary>
/// The latest complete safety census's work, not a first-seen cohort. The controller owns both forbidding and
/// allowing items. Held lists the safe forbidden stacks a safety hold kept forbidden, with reasons.
/// </summary>
public sealed record EventLootHistory
{
    public IReadOnlyList<StartingSupply> Pending { get; init; } = [];

    public IReadOnlyList<LootHold> Held { get; init; } = [];

    /// <summary>Throws <see cref="DomainException"/> when the history is not sorted, unique and valid.</summary>
    public void Validate()
    {
        for (var i = 0; i < Held.Count; i++)
        {
            var row = Held[i];
            DomainException.Require(
                row.Thing.Length != 0 && row.Reason.Length != 0 && !(i > 0 && string.CompareOrdinal(Held[i - 1].Thing, row.Thing) >= 0),
                "invalid event loot hold"
            );
        }
        for (var i = 0; i < Pending.Count; i++)
        {
            Pending[i].Validate();
            DomainException.Require(!(i > 0 && string.CompareOrdinal(Pending[i - 1].Thing, Pending[i].Thing) >= 0), "duplicate or unsorted event loot");
        }
    }
}

/// <summary>
/// One loose stack at the cell the latest census saw it. Thing identity is the key; the cell only tells the
/// planner where to read it, so a stack a builder hauls aside stays work.
/// </summary>
public sealed record StartingSupply
{
    public string Thing { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Forbid { get; init; }

    /// <summary>Throws <see cref="DomainException"/> when the supply is not a valid supply allow in a plausible cell.</summary>
    public void Validate()
    {
        _ = new SupplyAllow(Thing, Definition, Cell);
        DomainException.Require(Cell.X < 4096 && Cell.Z < 4096, "invalid starting supply cell");
    }
}

public static class EventLootRules
{
    /// <summary>
    /// Throws <see cref="DomainException"/> (the caller
    /// keeps <paramref name="previous"/>).
    /// </summary>
    public static (EventLootHistory History, bool Any) ReviewEventLoot(EquatableArray<LootItem> rows, EventLootHistory previous)
    {
        previous.Validate();
        var pending = new List<StartingSupply>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.Supply.Validate();
            DomainException.Require(seen.Add(row.Supply.Thing), "duplicate event loot item");
            if ((row.IsSafe && row.Forbidden) || (row.IsExposed && !row.Forbidden))
            {
                pending.Add(row.Supply with { Forbid = !row.IsSafe });
            }
        }
        var sorted = pending.OrderBy(s => s.Thing, StringComparer.Ordinal).ToList();
        return (new EventLootHistory { Pending = sorted }, sorted.Count > 0);
    }

}
