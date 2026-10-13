namespace RimGovernor.Brain.Policy;

// The Military department owns the armory and wardrobe stores: each standing gear room is one store over its
// free cells, and the gear RoomDemand is its capacity reading: a gear room is asked for while none stands and
// serviceable gear of its kind is held, and again when every store of its kind is full. Until the rooms stand,
// gear stays in the general store.

/// <summary>
/// A standing armory with every free cell within the weapon clearance of a prison: layout keeps the armory clear
/// of prisons, so this names a plan that predates the rule or ground it could not clear. (Go
/// <c>ErrArmoryNearPrison</c>.)
/// </summary>
public sealed class ArmoryNearPrisonException(string room)
    : DomainException($"the armory has no free cell clear of a prison, so no armory stockpile can be sited (room {room})");

internal sealed class MilitaryOwner : IStoreOwner, IRoomsAsker, IStoreErrorSource
{
    public Department Department => Department.Military;

    public IReadOnlyList<Store> Stores(StoreView view) => GearStores(view).Stores;

    /// <summary>The rooms the department asks layout for even before any store stands: its reading owns the gear RoomDemand.</summary>
    public IReadOnlyList<PlannedRole> RoomsAsked() => PolicyFunctions.GearRooms;

    /// <summary>Names a standing armory no store can be sited in (ArmoryNearPrisonException).</summary>
    public Exception? StoreErr(StoreView view) => GearStores(view).Err;

    /// <summary>
    /// Asks for a gear room while none stands and serviceable gear of its kind is held (weapons and armor for
    /// the armory, clothing for the wardrobe), and for a further one when every store of the kind is full.
    /// </summary>
    public RoomDemand RoomDemand(StoreView view)
    {
        var g = view.Gear;
        var ranges = PolicyFunctions.RangesWanted(view.TrainingStands);
        var rings = PolicyFunctions.RangesWanted(view.TrainingRings);
        if (g is null || view.Layout is null || view.Rooms is null)
        {
            return new RoomDemand { Known = g is not null, Ranges = ranges, Rings = rings };
        }
        var demand = PolicyFunctions.DeclaredDemand(view, Stores(view)) with { Known = true, Ranges = ranges, Rings = rings };
        var stands = new HashSet<PlannedRole>();
        foreach (var planned in view.Layout.Rooms)
        {
            if (PolicyFunctions.CensusRoomIn(planned, view.Rooms).Ok)
            {
                stands.Add(planned.Role);
            }
        }
        return demand with
        {
            Armory = demand.Armory || (!stands.Contains(PlannedRole.Armory) && g.Weapons + g.ArmorHeld > 0),
            Wardrobe = demand.Wardrobe || (!stands.Contains(PlannedRole.Wardrobe) && g.Clothing > 0),
        };
    }

    /// <summary>
    /// The armory and wardrobe stores: the first standing room of each kind, over its free cells. The armory
    /// never takes a cell within the weapon clearance of a prison (nearPrison); a room whose free cells all are
    /// is reported (ArmoryNearPrisonException), not silently left without a store.
    /// </summary>
    private static (List<Store> Stores, Exception? Err) GearStores(StoreView r)
    {
        if (r.Layout is null || r.Rooms is null || r.Gear is null)
        {
            return ([], null);
        }
        var prisons = PolicyFunctions.PrisonCells(r.Layout);
        var output = new List<Store>();
        Exception? err = null;
        (PlannedRole Module, string Prefix, StockpileFilter Filter)[] gears =
        [
            (PlannedRole.Armory, DomainConstants.ArmoryRolePrefix, r.Gear.Armory),
            (PlannedRole.Wardrobe, DomainConstants.WardrobeRolePrefix, r.Gear.Wardrobe),
        ];
        foreach (var gear in gears)
        {
            foreach (var planned in r.Layout.Rooms)
            {
                if (planned.Role != gear.Module)
                {
                    continue;
                }
                // Census: the stockpile zone is the room's own cells.
                var (room, ok) = PolicyFunctions.CensusRoomIn(planned, r.Rooms);
                if (!ok || room!.Cells.Count == 0)
                {
                    continue;
                }
                var site = new StoreSite
                {
                    Role = gear.Prefix + room.Id,
                    Interior = planned.Region.Bounds(),
                    Region = planned.Region,
                    Filter = gear.Filter,
                    Priority = StockpilePriority.Preferred,
                };
                if (gear.Module == PlannedRole.Armory)
                {
                    var free = PolicyFunctions.RoomPool(room.Cells, r.Cells, r.Protected);
                    var pool = free.Where(c => !PolicyFunctions.NearPrison([c], prisons)).ToList();
                    if (free.Count > 0 && pool.Count == 0)
                    {
                        err = new ArmoryNearPrisonException(room.Id);
                        break;
                    }
                    if (pool.Count < free.Count)
                    {
                        site = site with { Regions = PolicyFunctions.CellRects(PolicyFunctions.CellSet(pool)) };
                    }
                }
                output.Add(new Store { Site = site, Further = gear.Module });
                break;
            }
        }
        return (output, err);
    }
}
