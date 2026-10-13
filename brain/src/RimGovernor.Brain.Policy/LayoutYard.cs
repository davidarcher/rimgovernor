namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The plan's yards as Outdoor rooms, one per reservation in plan order.</summary>
    public List<PlannedRoom> YardRooms()
    {
        var output = new List<PlannedRoom>();
        foreach (var r in Reservations)
        {
            if (r.Kind == PolicyFunctions.ReserveYard)
            {
                output.Add(new PlannedRoom { Role = PolicyFunctions.PlannedYard, Region = PolicyFunctions.RectRegion(PolicyFunctions.Pad(r.Area.Bounds(), -1)), Outdoor = true });
            }
        }
        return output;
    }
}

/// <summary>Ports <c>layout_yard.go</c>: the materials yard, a planned Outdoor room beside the core.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The yard's reservation: its outline, the interior plus a one-cell margin.</summary>
    public static ReservationKind ReserveYard => ReservationKind.Yard;

    /// <summary>The plan role of a yard viewed as an Outdoor room.</summary>
    public static PlannedRole PlannedYard => PlannedRole.Yard;

    /// <summary>The yard's interior width (117 cells with the height).</summary>
    public const int YardW = 13;

    public const int YardH = 9;

    /// <summary>How many yards the plan should hold: one from the start, more when demand asks for them.</summary>
    public static int YardRoomsWanted(RoomDemand demand) => Math.Max(demand.Yard, 1);

    /// <summary>The yards demand asks for that plan lacks.</summary>
    public static int YardRoomsOwed(LayoutPlan plan, RoomDemand demand) => Math.Max(YardRoomsWanted(demand) - plan.YardRooms().Count, 0);

    /// <summary>Tops plan up to want yards, each on the free core-side ground nearest the core. Nothing placed moves; a yard that fits nowhere is left out.</summary>
    public static LayoutPlan PlanYardSites(LayoutPlan plan, int want)
    {
        var have = plan.YardRooms().Count;
        if (have >= want || plan.Rooms.Count == 0)
        {
            return plan;
        }
        var u = NewUtilityGrid(plan);
        var (w, h) = WalledSide(YardW, YardH);
        for (; have < want; have++)
        {
            if (u.Site(w, h, u.Cx, u.Cz) is not { } site)
            {
                break;
            }
            plan = u.Reserve(plan, new LayoutReservation { Kind = ReserveYard, Area = RectRegion(site) });
        }
        return plan;
    }
}
