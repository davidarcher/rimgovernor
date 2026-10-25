namespace RimGovernor.Brain.Policy;

/// <summary>
/// Pawn deaths drop forbidden items: a dead colonist's corpse and gear arrive
/// forbidden and are released by the admin/supply-safety Need's standing census (ReviewEventLoot), one stack at a time.
/// Release is selective: FilterLootRelease keeps back what a native spawner forbids on purpose and whatever lies
/// in danger.
/// </summary>
public static class LootReleaseRules
{
    /// <summary>Hold reasons FilterLootRelease records.</summary>
    public const string LootHoldSpawnForbidden = "spawn_forbidden";

    public const string LootHoldDanger = "danger";

    /// <summary>
    /// Narrows the census's release side (a safe, forbidden stack): a stack whose def a native spawner forbids on
    /// purpose is never released, and neither is one within ThreatReachCells of a danger seed (a live hostile, a
    /// hive: DangerSeeds). The danger seeds are required, alongside the native route check. Forbidding an unsafe stack and allowed stacks are untouched. The returned rows
    /// are the census the safety review acts on.
    /// </summary>
    public static (EquatableArray<LootItem> Rows, IReadOnlyList<LootHold> Holds) FilterLootRelease(EquatableArray<LootItem> rows, EquatableArray<Cell> danger)
    {
        var kept = new List<LootItem>();
        var holds = new List<LootHold>();
        foreach (var row in rows)
        {
            if (!row.IsSafe || !row.Forbidden)
            {
                kept.Add(row);
                continue;
            }
            var reason = "";
            if (row.SpawnForbidden)
            {
                reason = LootHoldSpawnForbidden;
            }
            else
            {
                if (ShelterRules.NearDanger(danger, row.Supply.Cell)) reason = LootHoldDanger;
            }
            if (reason == "")
            {
                kept.Add(row);
                continue;
            }
            holds.Add(new LootHold { Thing = row.Supply.Thing, Definition = row.Supply.Definition, Cell = row.Supply.Cell, Reason = reason });
        }
        return (EquatableArray.From(kept), [.. holds.OrderBy(h => h.Thing)]);
    }
}
