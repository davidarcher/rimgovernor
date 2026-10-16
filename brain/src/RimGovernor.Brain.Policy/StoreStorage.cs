namespace RimGovernor.Brain.Policy;

// The Storage department's stores: the warehouse over the whole interior of each planned storage room, and the
// materials yard over the whole interior of each planned yard. Each is one zone, sized once. Layout sizes the
// storage rooms from held stock; a full yard asks for a further yard through RoomDemand.

/// <summary>The Storage department.</summary>
internal sealed class StorageOwner : IStoreOwner
{
    public Department Department => Department.Storage;

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
        var gear = PolicyFunctions.GearPatchesOf(v.Layout).Cells();
        foreach (var room in v.PlannedStorageRooms())
        {
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = DomainConstants.GeneralRole,
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
                    Role = DomainConstants.YardRole,
                    Interior = room.Region.Bounds(),
                    Region = room.Region,
                    Filter = StockpileFilter.YardFilter(),
                    Priority = StockpilePriority.Low,
                },
                Further = PlannedRole.Yard,
            });
        }
        return output;
    }

    public RoomDemand RoomDemand(StoreView v) => PolicyFunctions.DeclaredDemand(v, Stores(v));
}
