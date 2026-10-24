namespace RimGovernor.Brain.Policy;

// The Military department owns the gear stores: a weapons zone and an apparel zone, fixed patches inside the
// planned warehouse (GearPatchesOf). They are warehouse zones like any other store: the warehouse leaves their
// cells out, and a full zone grows by the storage capacity options, not by a gear-specific room.

internal sealed class MilitaryOwner : IStoreOwner
{
    /// <summary>The weapons and apparel zones; none while the plan or the armor split is unread.</summary>
    public IReadOnlyList<Store> Stores(StoreView view)
    {
        if (view.Layout is null || view.Gear is null)
        {
            return [];
        }
        var patches = StoreRules.GearPatchesOf(view.Layout);
        var output = new List<Store>();
        (string Role, GearPatch Patch, StockpileFilter Filter)[] gears =
        [
            (StockpileRolesConstants.WeaponsRole, patches.Weapons, view.Gear.Weapons),
            (StockpileRolesConstants.ApparelRole, patches.Apparel, view.Gear.Apparel),
        ];
        foreach (var (role, patch, filter) in gears)
        {
            if (patch is not GearPatch.Placed { Square: var rect })
            {
                continue;
            }
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = role,
                    Exact = true,
                    Interior = rect,
                    Filter = filter,
                    Priority = StockpilePriority.Preferred,
                },
            });
        }
        return output;
    }

    /// <summary>The department's only ask of layout is the training range and ring.</summary>
    public RoomDemand RoomDemand(StoreView view) =>
        new() { Ranges = view.TrainingStands, Rings = view.TrainingRings };
}
