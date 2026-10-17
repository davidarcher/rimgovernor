namespace RimGovernor.Brain.Policy;

// The Storage department's stores: the warehouse over the whole interior of each planned storage room, and the
// materials yard over the whole interior of each planned yard. Each is one zone, sized once. Layout sizes the
// storage rooms from held stock; the yard asks for a further yard from its runway and stranded stock (YardDemand).

/// <summary>The Storage department.</summary>
internal sealed class StorageOwner : IStoreOwner
{
    /// <summary>
    /// The warehouses then the yards, in plan order. The warehouse is Low priority so the workstation
    /// stockpiles draw first; every store of a role shares its key, the zone matching its room by position.
    /// </summary>
    public IReadOnlyList<Store> Stores(StoreView v)
    {
        if (v.Layout is null)
        {
            return [];
        }
        var output = new List<Store>();
        var gear = StoreRules.GearPatchesOf(v.Layout).Cells();
        foreach (var room in v.PlannedStorageRooms())
        {
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = StockpileRolesConstants.GeneralRole,
                    Interior = room.Region.Bounds(),
                    Region = room.Region,
                    Avoid = gear,
                    Filter = StockpileFilter.GeneralFilter(),
                    Priority = StockpilePriority.Low,
                },
            });
        }
        foreach (var room in v.Layout.RoomsOf(PlannedRole.Yard))
        {
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = StockpileRolesConstants.YardRole,
                    Interior = room.Region.Bounds(),
                    Region = room.Region,
                    Filter = StockpileFilter.YardFilter(),
                    Priority = StockpilePriority.Low,
                },
            });
        }
        return output;
    }

    public RoomDemand RoomDemand(StoreView v) => new();
}
