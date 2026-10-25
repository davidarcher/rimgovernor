using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Narrows the loot census's Allow candidates to what may be released (<c>FilterLootRelease</c>:
    /// spawner-forbidden stacks and stacks in danger stay forbidden), then to the safety holds, before the safety
    /// review acts on it. The extent is the same derived extent the routines API reports. The holds are sorted by
    /// thing.
    /// </summary>
    internal static (EquatableArray<LootItem> Census, IReadOnlyList<LootHold> Held) LootReachFilter(RoundsRequest request)
    {
        var f = request.Facts;
        var (released, held) = LootReleaseRules.FilterLootRelease(f.EventLoot, f.DangerSeeds);
        if (!released.Any(row => row.IsSafe && row.Forbidden)) return (released, held);
        var (census, reach) = RemoteRules.FilterLootReach(released, RemoteRules.LootContext(f));
        return (census, [.. held.Concat(reach).OrderBy(hold => hold.Thing, StringComparer.Ordinal)]);
    }
}
