namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Reads the stranger tomb facts into the projection: the live buried stacks and whether the sarcophagus stuff is funded.</summary>
    internal ColonyProjection ReviewStrangers(GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        projection = projection with { Strangers = new StrangerTomb() };
        var (live, ok) = PolicyApi.KnowBuriedStacks(projection.Facts.MoodPawns);
        if (!ok)
        {
            return projection;
        }
        if (live >= PolicyApi.StrangerTombStackCap)
        {
            return projection with { Strangers = new StrangerTomb { Live = live } };
        }
        var targets = ResourceTargets(snapshot);
        return projection with { Strangers = new StrangerTomb { Live = live, Funded = projection.StuffFunded(projection.Shapes.Furniture.Sarcophagus, targets) } };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly string[] BurialDefinitions = ["Wall", "Door", PolicyApi.GraveDefinition];

    internal static TombStep TombStep(ColonyProjection facts)
    {
        if (!TombOwed(facts).TryGetValue(out var owed) || !owed)
        {
            return new TombStep();
        }
        SarcophagusAvailable(facts).TryGetValue(out var available);
        facts.LayoutPlan.TryGetValue(out var plan);
        facts.Facts.Waste.TryGetValue(out var waste);
        facts.Facts.CurrentConstruction.TryGetValue(out var built);
        return PolicyApi.NextTombStep(plan ?? new LayoutPlan(), waste, built?.Buildings ?? [], facts.Shapes, available, facts.Strangers);
    }

    internal static Fact<bool> TombOwed(ColonyProjection facts) =>
        PolicyApi.TombOwed(facts.Shapes, SarcophagusAvailable(facts), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction, facts.Strangers);

    internal static Fact<bool> SarcophagusAvailable(ColonyProjection facts)
    {
        foreach (var d in facts.Definitions)
        {
            if (d.Name != facts.Shapes.Furniture.Sarcophagus)
            {
                continue;
            }
            if (d.Stuffed && d.StuffOptions.Count == 0)
            {
                return Known(false);
            }
            return d.Available;
        }
        return Unknown<bool>();
    }

    internal static (PlannedRoom Room, bool Owed) PlannedMorgue(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var wk = facts.Facts.Waste.TryGetValue(out var waste);
        var (ground, gk) = ColonyGround(facts);
        if (!pk || !wk || !gk)
        {
            return (new PlannedRoom(), false);
        }
        var room = PolicyApi.MorgueRoomOwed(plan!, plan!.GroundWithRock(ground, NaturalRock(facts)), waste!);
        return room is null ? (new PlannedRoom(), false) : (room, true);
    }

    internal static Fact<bool> BurialOwed(ColonyProjection facts)
    {
        var tk = TombOwed(facts).TryGetValue(out var tomb);
        if (PlannedMorgue(facts).Owed || (tk && tomb))
        {
            return Known(true);
        }
        return tk ? Known(false) : Unknown<bool>();
    }

    internal static BurialCensus? BurialCensus(ColonyProjection facts)
    {
        var wk = facts.Facts.Waste.TryGetValue(out var waste);
        var bk = facts.Facts.CurrentConstruction.TryGetValue(out var built);
        var ak = SarcophagusAvailable(facts).TryGetValue(out var available);
        if (!wk || !bk || !built!.Colony || !ak)
        {
            return null;
        }
        return new BurialCensus { Waste = waste!, Built = built.Buildings, Shapes = facts.Shapes, Sarcophagus = available };
    }

    internal static (PlannedRoom Room, Fact<bool> Owed) PlannedMealCloset(ColonyProjection facts)
    {
        var ak = facts.DefinitionAvailable("Cooler").TryGetValue(out var available);
        if (ak && !available)
        {
            return (new PlannedRoom(), Known(false));
        }
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        if (!ak || !pk || !rk)
        {
            return (new PlannedRoom(), Unknown<bool>());
        }
        var (room, owed) = plan!.MealClosetOwed(rooms!);
        return (room ?? new PlannedRoom(), Known(owed));
    }

    internal static Fact<bool> MealClosetOwed(ColonyProjection facts) => PlannedMealCloset(facts).Owed;

    internal static Fact<EquatableArray<string>> WarmCoolingRooms(ColonyProjection facts) =>
        PolicyApi.WarmCoolingRooms(facts.DefinitionAvailable("Cooler"), facts.LayoutPlan, facts.Rooms);
}
