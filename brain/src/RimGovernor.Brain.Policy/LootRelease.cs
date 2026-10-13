namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>loot_release.go</c>. Pawn deaths drop forbidden items: a dead colonist's corpse and gear arrive
/// forbidden and are released by ManageSupplySafety's standing census (ReviewEventLoot), one stack at a time.
/// Release is selective: FilterLootRelease keeps back what a native spawner forbids on purpose and whatever lies
/// in danger.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>Hold reasons FilterLootRelease records.</summary>
    public const string LootHoldSpawnForbidden = "spawn_forbidden";

    public const string LootHoldDanger = "danger";

    /// <summary>
    /// Narrows the census's release side (a safe, forbidden stack): a stack whose def a native spawner forbids on
    /// purpose is never released, and neither is one within ThreatReachCells of a danger seed (a live hostile, a
    /// hive: DangerSeeds). Unknown seeds hold nothing back: the native census's own route check still refuses an
    /// item a live hostile can see. Forbidding an unsafe stack and allowed stacks are untouched. The returned rows
    /// are the census the safety review acts on.
    /// </summary>
    public static (Fact<EquatableArray<LootItem>> Rows, IReadOnlyList<LootHold> Holds) FilterLootRelease(Fact<EquatableArray<LootItem>> observed, Fact<EquatableArray<Cell>> seeds)
    {
        if (!observed.TryGetValue(out var rows))
        {
            return (observed, []);
        }
        seeds.TryGetValue(out var danger);

        var kept = new List<LootItem>();
        var holds = new List<LootHold>();
        foreach (var row in rows)
        {
            if (!row.SafetyKnown || !row.SafeToHaul || !row.Forbidden)
            {
                kept.Add(row);
                continue;
            }
            var reason = "";
            if (row.SpawnForbidden)
            {
                reason = LootHoldSpawnForbidden;
            }
            else if (NearDanger(danger, row.Supply.Cell))
            {
                reason = LootHoldDanger;
            }
            if (reason == "")
            {
                kept.Add(row);
                continue;
            }
            holds.Add(new LootHold { Thing = row.Supply.Thing, Definition = row.Supply.Definition, Cell = row.Supply.Cell, Reason = reason });
        }
        return (Known(EquatableArray.From(kept)), [.. holds.OrderBy(h => h.Thing)]);
    }
}
