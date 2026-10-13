namespace RimGovernor.Brain.Policy;

// The Shelter department's one store: the starter shelter's 3x3 starter-storage patch as a general store at the
// lowest priority, so the colony has somewhere to put its things before it has a storage room. The patch is the
// one the shelter interior template keeps clear, because the placement search treats zoned cells as unplaceable
// (tracked by the layout epic); a whole-interior zone starved the campfire of a slot. Any real store outranks
// it, and the zone is deleted (its contents haul to the better stores) once a storage room or freezer stands,
// or the shelter leaves the plan.

/// <summary>The Shelter department.</summary>
internal sealed class ShelterOwner : IStoreOwner
{
    public Department Department => Department.Shelter;

    /// <summary>
    /// The shelter rooms' general stores while no storage room stands. Nothing is declared while the layout
    /// plan is unread; a plan with no shelter or a standing storage room declares the role retired, which
    /// deletes the zone. While the room census is unread the stores stay: absence of a reading is not a
    /// standing room.
    /// </summary>
    public IReadOnlyList<Store> Stores(StoreView v)
    {
        if (v.Layout is null)
        {
            return [];
        }
        var retired = new Store { Retired = true, Site = new StoreSite { Role = DomainConstants.ShelterRole } };
        if (v.StorageRoomStands())
        {
            return [retired];
        }
        var output = new List<Store>();
        foreach (var room in v.Layout.Rooms)
        {
            if (room.Role == PlannedRole.Shelter)
            {
                Rectangle patch;
                try
                {
                    patch = PolicyFunctions.StarterStorage(room.Footprint());
                }
                catch (DomainException)
                {
                    continue;
                }
                if (patch.Width == 0)
                {
                    continue;
                }
                output.Add(new Store
                {
                    Site = new StoreSite
                    {
                        Role = DomainConstants.ShelterRole,
                        Interior = patch,
                        Filter = StockpileFilter.GeneralFilter(),
                        Priority = StockpilePriority.Low,
                    },
                });
            }
        }
        return output.Count == 0 ? [retired] : output;
    }

    public RoomDemand RoomDemand(StoreView v) => new();
}

public sealed partial record StoreView
{
    /// <summary>
    /// Whether a planned storage room or freezer is a roofed room the census sees: the colony has a real store
    /// the shelter's floor yields to.
    /// </summary>
    internal bool StorageRoomStands()
    {
        if (Layout is null || Rooms is null)
        {
            return false;
        }
        foreach (var planned in Layout.Rooms)
        {
            if (planned.Role != PlannedRole.Storage && planned.Role != PlannedRole.Freezer)
            {
                continue;
            }
            if (PolicyFunctions.CensusRoomIn(planned, Rooms).Ok)
            {
                return true;
            }
        }
        return false;
    }
}
