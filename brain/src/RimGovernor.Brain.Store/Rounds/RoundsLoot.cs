using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Narrows the loot census's Allow candidates to what may be released (<c>FilterLootRelease</c>:
    /// spawner-forbidden stacks and stacks in danger stay forbidden), then to the safety holds, before the safety
    /// review acts on it. The extent is the same derived extent the routines API reports. The holds are sorted by
    /// thing. An unknown census is returned as it is, with no holds.
    /// </summary>
    internal static (Fact<EquatableArray<LootItem>> Census, IReadOnlyList<LootHold> Held) LootReachFilter(RoundsRequest request)
    {
        var f = request.Facts;
        if (!f.EventLoot.IsKnown)
        {
            return (f.EventLoot, []);
        }
        var (released, held) = PolicyFunctions.FilterLootRelease(f.EventLoot, f.DangerSeeds);
        var (census, reach) = PolicyFunctions.FilterLootReach(released, PolicyFunctions.LootContext(f));
        return (census, [.. held.Concat(reach).OrderBy(hold => hold.Thing, StringComparer.Ordinal)]);
    }
}
