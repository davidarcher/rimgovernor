namespace RimGovernor.Brain.Policy;

// Cooling for the standing tomb, morgue and meal closet. Once the colony can build coolers, each of those rooms
// that stands and measures above TombMaxC owes cooling, empty or not: cold keeps a colonist fresh for resurrector
// mech serum and slows rot, and the room is cooled before anything lies in it. MaintainRefrigeration takes the
// room beside its food rooms, reuses or builds a cooler (the planned one over the room's exhaust reservation
// first) and sets it to FreezerTargetC. Cooling never gates a room's shell.

public static partial class PolicyFunctions
{
    /// <summary>The warmest a standing cooling room may measure before it owes cooling: corpses stop rotting at freezing.</summary>
    public const double TombMaxC = 0.0;

    /// <summary>
    /// Lists the native room IDs of the standing planned tombs, morgues and meal closets that measure warmer than
    /// TombMaxC, sorted. Known empty while coolers are unavailable; unknown while a fact it reads is.
    /// </summary>
    public static Fact<EquatableArray<string>> WarmCoolingRooms(Fact<bool> coolers, Fact<LayoutPlan> plan, Fact<RoomObservation> rooms)
    {
        var ak = coolers.TryGetValue(out var available);
        if (ak && !available)
        {
            return Known<EquatableArray<string>>([]);
        }
        var pk = plan.TryGetValue(out var p);
        var rk = rooms.TryGetValue(out var r);
        if (!ak || !pk || !rk)
        {
            return Unknown<EquatableArray<string>>();
        }
        var output = new List<string>();
        foreach (var planned in p!.Rooms)
        {
            if (planned.Role != PlannedRole.Tomb && planned.Role != PlannedRole.Morgue && planned.Role != PlannedRole.MealCloset)
            {
                continue;
            }
            // Census: the room's cells and floors are cooled.
            var (room, ok) = CensusRoomIn(planned, r!);
            if (!ok)
            {
                continue;
            }
            if (!room!.Temperature.TryGetValue(out var t))
            {
                return Unknown<EquatableArray<string>>();
            }
            if (t > TombMaxC)
            {
                output.Add(room.Id);
            }
        }
        output.Sort(StringComparer.Ordinal);
        return Known(EquatableArray.From(output));
    }

    /// <summary>Adds the warm cooling rooms to the review's rooms: any warm one makes it active. Unknown rooms leave the review as it is.</summary>
    public static RefrigerationReview WithWarmRooms(this RefrigerationReview r, Fact<EquatableArray<string>> warm)
    {
        if (!warm.TryGetValue(out var ids) || ids.Count == 0)
        {
            return r;
        }
        var rooms = new List<string>(r.Rooms);
        var seen = new HashSet<string>(rooms, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (seen.Add(id))
            {
                rooms.Add(id);
            }
        }
        rooms.Sort(StringComparer.Ordinal);
        return r with { Rooms = EquatableArray.From(rooms), Active = true, Warm = EquatableArray.From(ids) };
    }
}
