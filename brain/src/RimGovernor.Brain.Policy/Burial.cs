namespace RimGovernor.Brain.Policy;

// Go: burial.go
// The admin/burial Need (AdminFamily.Burial) is the People department's burial: the tomb's sarcophagus or the
// plain grave near the base, the morgue's shell, and the tomb and morgue stores. It is met while no human corpse is
// owed a shell, a sarcophagus or a grave. Vanilla haulers carry the corpses; nothing here hauls.

public static partial class PolicyFunctions
{
    /// <summary>
    /// Ranks the morgue below graves and sarcophagi, which take a colonist corpse the morgue holds until one
    /// has room, and above the Low corpse dump. The native acceptance case burial/grave_over_morgue verifies
    /// the ranking.
    /// </summary>
    public const StockpilePriority MorguePriority = StockpilePriority.Normal;
}

/// <summary>
/// The People department's stores: the tomb and the morgue. A plain grave is a building that stores its one
/// corpse and needs no zone, and the burial asks layout for no room (graves are sited near the base, GraveSites).
/// </summary>
public sealed class BurialOwner : IStoreOwner
{
    public IReadOnlyList<Store> Stores(StoreView view)
    {
        if (view.Layout is null)
        {
            return [];
        }

        var output = new List<Store>();
        var roles = new (PlannedRole Role, string Prefix, StockpileFilter Filter, StockpilePriority Priority)[]
        {
            (PlannedRole.Tomb, DomainConstants.TombRolePrefix, StockpileFilter.TombCorpsesFilter(), StockpilePriority.Critical),
            (PlannedRole.Morgue, DomainConstants.MorgueRolePrefix, StockpileFilter.MorgueCorpsesFilter(), PolicyFunctions.MorguePriority),
        };
        foreach (var s in roles)
        {
            // The first planned room of the role holds the store.
            foreach (var room in view.Layout.RoomsOf(s.Role))
            {
                var bounds = room.Region.Bounds();
                var key = $"{bounds.X}_{bounds.Z}";
                output.Add(
                    new Store
                    {
                        Site = new StoreSite
                        {
                            Role = s.Prefix + key,
                            Interior = bounds,
                            Region = room.Region,
                            Filter = s.Filter,
                            Priority = s.Priority,
                        },
                    }
                );
                break;
            }
        }

        return output;
    }

    public RoomDemand RoomDemand(StoreView view) => new();
}
