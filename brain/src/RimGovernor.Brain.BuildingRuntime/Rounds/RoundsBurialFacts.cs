namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsBurialFacts
{
    internal static readonly string[] BurialDefinitions = ["Wall", "Door", TombRules.GraveDefinition];

    internal static TombStep TombStep(ColonyProjection facts)
    {
        if (!TombOwed(facts))
        {
            return new TombStep();
        }
        SarcophagusAvailable(facts).TryGetValue(out var available);
        var plan = facts.LayoutPlan;
        var waste = facts.Facts.Waste;
        return TombRules.NextTombStep(plan ?? new LayoutPlan(), waste, facts.Facts.CurrentConstruction?.Buildings ?? [], facts.Shapes, available);
    }

    internal static bool TombOwed(ColonyProjection facts) =>
        TombRules.TombOwed(facts.Shapes, SarcophagusAvailable(facts), facts.LayoutPlan, facts.Facts.Waste, facts.Facts.CurrentConstruction);

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
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
        var waste = facts.Facts.Waste;
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        if (!pk || !gk)
        {
            return (new PlannedRoom(), false);
        }
        var room = MorgueRules.MorgueRoomOwed(plan!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), waste!);
        return room is null ? (new PlannedRoom(), false) : (room, true);
    }

    /// <summary>
    /// A human corpse waits and the plan holds no morgue yet: the burial demand asks layout for one
    /// (RoomDemand.Morgue) and the burial waits for the replan to plan it.
    /// </summary>
    internal static bool MorgueAwaitingRoom(ColonyProjection facts) =>
        MorgueRules.MorgueWaiting(facts.Facts.Waste)
        && facts.LayoutPlan is { } plan && plan.RoomsOf(PlannedRole.Morgue).Count == 0;

    /// <summary>
    /// The burial's asks of the layout plan (RoomDemand.Tomb, RoomDemand.Morgue): another tomb while a dead
    /// colonist is owed a sarcophagus no planned tomb has room for, a morgue while a human corpse waits.
    /// </summary>
    internal static RoomDemand BurialDemand(ColonyProjection facts, LayoutPlan plan)
    {
        var tomb = TombStep(facts).Kind == TombStepKind.Full ? plan.TombRooms() + 1 : 0;
        var morgue = MorgueRules.MorgueWaiting(facts.Facts.Waste) ? 1 : 0;
        return new RoomDemand { Tomb = tomb, Morgue = morgue };
    }

    internal static Fact<bool> BurialOwed(ColonyProjection facts) =>
        Known(PlannedMorgue(facts).Owed || MorgueAwaitingRoom(facts) || TombOwed(facts));

    internal static (PlannedRoom Room, Fact<bool> Owed) PlannedMealCloset(ColonyProjection facts)
    {
        var ak = facts.DefinitionAvailable("Cooler").TryGetValue(out var available);
        if (ak && !available)
        {
            return (new PlannedRoom(), Known(false));
        }
        var plan = facts.LayoutPlan;
        var pk = plan is not null;
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
        TombRules.WarmCoolingRooms(facts.DefinitionAvailable("Cooler"), facts.LayoutPlan, facts.Rooms);
}
