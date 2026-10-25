namespace RimGovernor.Brain.Policy;

public sealed record LootItem
{
    public required RimGovernor.Brain.GameFacts.Observations.LootItem Observed { get; init; }
    public required RimGovernor.Brain.GameFacts.Observations.EntityRef Entity { get; init; }

    /// <summary>The allow/forbid work identity derived only when policy selects the stack.</summary>
    public StartingSupply Supply => new()
    {
        Thing = Observed.Item.Id ?? throw new RequiredFactException("loot stack id"),
        Definition = Entity.DefName,
        Cell = Entity.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap at
            ? at.Cell : throw new RequiredFactException("loot stack map cell"),
    };
    public bool Forbidden => Observed.Forbidden;
    public RimGovernor.Brain.GameFacts.Observations.HaulSafety Haul => Observed.Safety;
    public bool IsSafe => Haul is RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe;
    public bool IsExposed => Haul is RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unsafe;
    public bool IsUnreached => Haul is RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unreached;
    public bool SafePath(out double pathLength)
    {
        pathLength = Haul is RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength: var path } ? path : 0;
        return IsSafe;
    }

    /// <summary>The catalog's spawner forbids this product intentionally.</summary>
    public bool SpawnForbidden { get; init; }
    public long Count => Observed.Count;
    public long StorageHeadroom => Observed.StorageHeadroom;
    public EquatableArray<string> AcceptedBy => EquatableArray.From(Observed.AcceptedBy.Select(reference =>
        reference.Id ?? throw new RequiredFactException("loot accepting storage id")));
    public RimGovernor.Brain.GameFacts.Observations.LootDeterioration? Deterioration => Observed.Deterioration;
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
