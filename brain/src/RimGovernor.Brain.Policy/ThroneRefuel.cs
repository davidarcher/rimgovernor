namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
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
    /// The refuel due for the throne room's unlit lights: the lowest-ID lamp of a glowing definition standing
    /// inside the title's room that the census reads unlit and out of fuel, ordered through the refuel work
    /// giver for the best hauler (the rearm's pick). None while the room is not planned and standing, or when no
    /// unlit lamp is measured empty (an unknown fuel state is neither a deficit nor a refuel).
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
        if (!CensusRoomIn(room, rooms).Ok)
        {
            return new ThroneStep();
        }
        var glow = need.GlowDefNames();
        var best = "";
        foreach (var l in lamps)
        {
            if (!glow.Contains(l.Definition) || !room.Region.Bounds().Contains(l.Cell) || l.Lit)
            {
                continue;
            }
            if (!l.OutOfFuel.TryGetValue(out var empty) || !empty)
            {
                continue;
            }
            if (best.Length == 0 || string.CompareOrdinal(l.Id, best) < 0)
            {
                best = l.Id;
            }
        }
        var (pawn, found) = DefenseRearmPawn(workers);
        if (best.Length == 0 || !found)
        {
            return new ThroneStep();
        }
        return new ThroneStep
        {
            Kind = ThroneStepKind.Refuel,
            Room = room,
            Need = need,
            Lamp = best,
            Pawn = pawn,
        };
    }
}
