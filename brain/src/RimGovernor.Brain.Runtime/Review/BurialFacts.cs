namespace RimGovernor.Brain.Runtime;

internal static class BurialFacts
{
    internal static readonly string[] BurialDefinitions = [nameof(RimWorld.ThingDefOf.Wall), nameof(RimWorld.ThingDefOf.Door), TombRules.GraveDefinition];

    internal static TombStep TombStep(ColonyReading facts)
    {
        if (!TombOwed(facts))
        {
            return new TombStep();
        }
        var available = SarcophagusAvailable(facts);
        var plan = facts.Facts.LayoutPlan;
        var waste = facts.Facts.Waste;
        return TombRules.NextTombStep(plan ?? new LayoutPlan(), waste, facts.Facts.CurrentConstruction.Buildings, facts.Facts.Shapes, available);
    }

    internal static bool TombOwed(ColonyReading facts) =>
        TombRules.TombOwed(facts.Facts.Shapes, SarcophagusAvailable(facts), facts.Facts.LayoutPlan, facts.Facts.Waste, facts.Facts.CurrentConstruction);

    internal static bool SarcophagusAvailable(ColonyReading facts)
    {
        foreach (var d in facts.Facts.Definitions)
        {
            if (d.Name != facts.Facts.Shapes.Furniture.Sarcophagus)
            {
                continue;
            }
            if (d.Stuffed && d.StuffOptions.Count == 0)
            {
                return false;
            }
            return d.Available;
        }
        throw new RequiredFactException("sarcophagus definition");
    }

    internal static (PlannedRoom Room, bool Owed) PlannedMorgue(ColonyReading facts)
    {
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var waste = facts.Facts.Waste;
        var ground = BuildingPlanner.ColonyGround(facts);
        if (!pk)
        {
            return (new PlannedRoom(), false);
        }
        var room = MorgueRules.MorgueRoomOwed(plan!, plan!.GroundWithRock(ground, BuildingPlanner.NaturalRock(facts)), waste!);
        return room is null ? (new PlannedRoom(), false) : (room, true);
    }

    /// <summary>
    /// A human corpse waits and the plan holds no morgue yet: the burial demand asks layout for one
    /// (RoomDemand.Morgue) and the burial waits for the replan to plan it.
    /// </summary>
    internal static bool MorgueAwaitingRoom(ColonyReading facts) =>
        MorgueRules.MorgueWaiting(facts.Facts.Waste)
        && facts.Facts.LayoutPlan is { } plan && plan.RoomsOf(PlannedRole.Morgue).Count == 0;

    /// <summary>
    /// The burial's asks of the layout plan (RoomDemand.Tomb, RoomDemand.Morgue): another tomb while a dead
    /// colonist is owed a sarcophagus no planned tomb has room for, a morgue while a human corpse waits.
    /// </summary>
    internal static RoomDemand BurialDemand(ColonyReading facts, LayoutPlan plan)
    {
        var tomb = TombStep(facts).Kind == TombStepKind.Full ? plan.TombRooms() + 1 : 0;
        var morgue = MorgueRules.MorgueWaiting(facts.Facts.Waste) ? 1 : 0;
        return new RoomDemand { Tomb = tomb, Morgue = morgue };
    }

    internal static bool BurialOwed(ColonyReading facts) =>
        PlannedMorgue(facts).Owed || MorgueAwaitingRoom(facts) || TombOwed(facts);

    internal static (PlannedRoom Room, bool Owed) PlannedMealCloset(ColonyReading facts)
    {
        var available = ColonyPlanningRules.DefinitionAvailable(facts.Facts, nameof(RimWorld.ThingDefOf.Cooler));
        if (!available)
        {
            return (new PlannedRoom(), false);
        }
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Facts.Upkeep.Rooms;
        if (!pk)
        {
            return (new PlannedRoom(), false);
        }
        var (room, owed) = plan!.MealClosetOwed(rooms!);
        return (room ?? new PlannedRoom(), owed);
    }

    internal static bool MealClosetOwed(ColonyReading facts) => PlannedMealCloset(facts).Owed;

    internal static EquatableArray<string> WarmCoolingRooms(ColonyReading facts) =>
        TombRules.WarmCoolingRooms(RefrigerationRules.CoolerDefinitions(facts.Facts.Stations).Any(candidate => ColonyPlanningRules.DefinitionAvailable(facts.Facts, candidate.Name)), facts.Facts.LayoutPlan, facts.Facts.Upkeep.Rooms);
}
