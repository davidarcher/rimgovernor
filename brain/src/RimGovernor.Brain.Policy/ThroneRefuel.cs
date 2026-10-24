namespace RimGovernor.Brain.Policy;

public static partial class ThroneRules
{
    /// <summary>
    /// The definitions the title wants lit: every def of its Glowing requirement sets and of its any-of count
    /// requirements (the braziers), sorted and unique. Names are the mirror's, never a C# list.
    /// </summary>
    public static IReadOnlyList<string> GlowDefNames(this ThroneNeed need)
    {
        var names = new List<string>();
        foreach (var set in need.Glowing)
        {
            names.AddRange(set);
        }
        foreach (var r in need.AnyOfCounts)
        {
            names.AddRange(r.Things);
        }
        names.Sort(StringComparer.Ordinal);
        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The refuel due for the throne room's unlit lights: some lamp of a glowing definition standing inside the
    /// title's room that the census reads unlit and out of fuel, with a hauler to refuel it (the rearm's pick).
    /// None while the room is not planned and standing, without a hauler, or when no unlit lamp is measured empty.
    /// </summary>
    public static ThroneStep NextThroneRefuel(
        LayoutPlan plan,
        RoomObservation rooms,
        ThroneNeed need,
        IReadOnlyList<Lamp> lamps,
        IReadOnlyList<WorkPawn> workers
    )
    {
        var (room, ok) = plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return new ThroneStep();
        }
        // Census: the lamp refuel needs the roofed room, not just a ring.
        if (!PlannedRoom.CensusRoomIn(room, rooms).Ok)
        {
            return new ThroneStep();
        }
        var glow = need.GlowDefNames();
        var unlit = lamps.Any(l => glow.Contains(l.Definition) && LayoutPerimeterRules.Contains(room.Region.Bounds(), l.Cell) && !l.Lit
            && l.Fuel == LampFuel.Empty);
        if (!unlit || !DefenseRearm.DefenseRearmPawn(workers, out _))
        {
            return new ThroneStep();
        }
        return new ThroneStep
        {
            Kind = ThroneStepKind.Refuel,
            Room = room,
            Need = need,
        };
    }
}
