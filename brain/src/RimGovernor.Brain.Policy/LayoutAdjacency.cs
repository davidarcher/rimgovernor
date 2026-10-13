namespace RimGovernor.Brain.Policy;

// The dining room's cold meal closet is the planned role PlannedRole.MealCloset (wire name "meal_closet"): a
// 2x2 (or 1x2) room behind the dining room's back wall, its only door in that wall, cooled by one cooler
// venting outdoors. The plan adds it only when no freezer shares a door with the dining room; the Critical meal
// stockpile then moves into it, where meals never rot.
public static partial class PolicyFunctions
{
    /// <summary>
    /// The planned meal closet with nothing standing on it while the planned dining room stands;
    /// MaintainRefrigeration shells it.
    /// </summary>
    public static (PlannedRoom? Room, bool Ok) MealClosetOwed(this LayoutPlan p, RoomObservation rooms)
    {
        PlannedRoom? closet = null;
        PlannedRoom? dining = null;
        foreach (var r in p.Rooms)
        {
            if (r.Role == PlannedRole.MealCloset && closet is null)
            {
                closet = r;
            }
            else if (r.Role == PlannedRole.Dining && dining is null)
            {
                dining = r;
            }
        }
        if (closet is null || dining is null)
        {
            return (null, false);
        }

        // Census: the closet shells only once dining is a usable room.
        if (!CensusRoomIn(dining, rooms).Ok)
        {
            return (null, false);
        }

        // Census: an unroofed closet ring is still owed its shell.
        if (CensusRoomIn(closet, rooms).Ok)
        {
            return (null, false);
        }
        return (closet, true);
    }

    /// <summary>Reports a planned freezer sharing a door with dining: a Link of either room in the wall between them.</summary>
    public static bool FreezerOpensInto(this LayoutPlan p, PlannedRoom dining) => p.FreezerDoorInto(dining).Ok;

    /// <summary>The first planned freezer sharing a door with dining, and that door.</summary>
    public static (PlannedRoom? Freezer, Cell Door, bool Ok) FreezerDoorInto(this LayoutPlan p, PlannedRoom dining)
    {
        foreach (var f in p.Rooms)
        {
            if (f.Role != PlannedRole.Freezer)
            {
                continue;
            }
            foreach (var link in p.LinksOf(f).Concat(p.LinksOf(dining)))
            {
                if (InWall(f.Region.Bounds(), link) && InWall(dining.Region.Bounds(), link))
                {
                    return (f, link, true);
                }
            }
        }
        return (null, default, false);
    }
}
