namespace RimGovernor.Brain.Policy;

public sealed record LootItem
{
    public StartingSupply Supply { get; init; } = new();

    public bool Forbidden { get; init; }

    public bool SafeToHaul { get; init; }

    public bool SafetyKnown { get; init; }

    /// <summary>
    /// Marks a def a native spawner makes forbidden on purpose (a hive's insect jelly:
    /// CompProperties_Spawner.spawnForbidden in the def mirror); such a stack is never released
    /// (<c>FilterLootRelease</c>).
    /// </summary>
    public bool SpawnForbidden { get; init; }

    /// <summary>Feeds remote loot's reach and demand filter; unknown cost or storage holds a remote stack.</summary>
    public long Count { get; init; }

    /// <summary>Feeds remote loot's reach and demand filter; unknown cost or storage holds a remote stack.</summary>
    public Fact<double> PathLength { get; init; }

    /// <summary>Feeds remote loot's reach and demand filter; unknown cost or storage holds a remote stack.</summary>
    public Fact<long> StorageHeadroom { get; init; }
}

/// <summary>
/// The latest complete safety census's work, not a first-seen cohort. The controller owns both forbidding and
/// allowing items. Held lists the safe forbidden stacks a safety hold kept forbidden, with reasons.
/// (Go <c>EventLootHistory</c>.)
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
/// planner where to read it, so a stack a builder hauls aside stays work. (Go <c>StartingSupply</c>.)
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

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>ReviewEventLoot</c>. Throws <see cref="DomainException"/> where Go returned an error (the caller
    /// keeps <paramref name="previous"/>).
    /// </summary>
    public static (EventLootHistory History, Fact<bool> Any) ReviewEventLoot(Fact<EquatableArray<LootItem>> observed, EventLootHistory previous)
    {
        previous.Validate();
        if (!observed.TryGetValue(out var rows))
        {
            return (previous, Unknown<bool>());
        }
        var pending = new List<StartingSupply>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            row.Supply.Validate();
            DomainException.Require(seen.Add(row.Supply.Thing), "duplicate event loot item");
            if (row.SafetyKnown && row.Forbidden == row.SafeToHaul)
            {
                pending.Add(row.Supply with { Forbid = !row.SafeToHaul });
            }
        }
        var sorted = pending.OrderBy(s => s.Thing, StringComparer.Ordinal).ToList();
        return (new EventLootHistory { Pending = sorted }, Known(sorted.Count > 0));
    }

    /// <summary>
    /// Go <c>supplySafetyPriority</c>, a Standard's: UnsafeLootSafeguard refuses allowing unsafe loot at
    /// dispatch, so unsafe loot raises no emergency.
    /// </summary>
    internal static int SupplySafetyPriority(RoundsFacts f) => f.EventLoot.IsKnown ? 2 : 4;
}
