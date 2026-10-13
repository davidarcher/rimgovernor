namespace RimGovernor.Brain.Policy;

// The Storage department's stores: the warehouse over the whole interior of each planned storage room, and the
// materials yard over the whole interior of each planned yard. Each is one zone, sized once; a full store asks
// layout for a further room through RoomDemand.

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
        foreach (var room in v.PlannedStorageRooms())
        {
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = DomainConstants.GeneralRole,
                    Interior = room.Region.Bounds(),
                    Region = room.Region,
                    Filter = StockpileFilter.GeneralFilter(),
                    Priority = StockpilePriority.Low,
                },
                Further = PlannedRole.Storage,
            });
        }
        foreach (var room in v.Layout.YardRooms())
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

    /// <summary>DeclaredDemand over the stores, held while a gear room the plan holds is not yet standing: it will take gear out of the warehouse.</summary>
    public RoomDemand RoomDemand(StoreView v)
    {
        var demand = PolicyFunctions.DeclaredDemand(v, Stores(v));
        if (v.Layout is not null && v.Rooms is not null && v.GearRoomPending(new MilitaryOwner().RoomDemand(v)))
        {
            demand = demand with { Storage = 0 };
        }
        return demand;
    }
}
