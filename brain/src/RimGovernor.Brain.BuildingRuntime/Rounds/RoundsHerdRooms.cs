using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsHerdRooms
{
    internal static readonly string[] HerdDefinitions =
    [
        "Wall",
        "Door",
        HerdPenRules.PenFenceDefinition,
        HerdPenRules.PenGateDefinition,
        HerdPenRules.PenMarkerDefinition,
    ];

    /// <summary>The animal furniture the herd rooms are built from (the ward's bed, the pen's marker); unusable when a definition is unavailable or unsized, and a control fault when native never read it.</summary>
    internal static (HerdFurniture Furniture, bool Usable) HerdFurnitureOf(ColonyProjection facts)
    {
        var output = new HerdFurniture();
        var usable = true;
        foreach (var name in new[] { facts.Shapes.Furniture.AnimalBed })
        {
            var d = RoundsAnimalContainment.AnimalContainmentDefinition(facts.Definitions, name);
            if (d is null)
            {
                throw new ControlException($"herdFurniture: {name} was not read from the native catalog");
            }

            var sizeKnown = d.Size.TryGetValue(out var size);
            var availableKnown = d.Available.TryGetValue(out var available);
            if (availableKnown && !available && !sizeKnown)
            {
                throw new ControlException($"herdFurniture: the native catalog has no definition {name}");
            }

            if (!availableKnown || !available || !sizeKnown || size.Width < 1 || size.Height < 1)
            {
                usable = false;
                continue;
            }

            output = output with { Bed = new InteriorPieceDef { Def = name, Size = new Cell(size.Width, size.Height) } };
        }

        var marker = RoundsAnimalContainment.AnimalContainmentDefinition(facts.Definitions, HerdPenRules.PenMarkerDefinition);
        if (marker is not null)
        {
            var sizeKnown = marker.Size.TryGetValue(out var size);
            if (marker.Available.TryGetValue(out var available) && available && sizeKnown && size.Width >= 1 && size.Height >= 1)
            {
                output = output with { Marker = new InteriorPieceDef { Def = HerdPenRules.PenMarkerDefinition, Size = new Cell(size.Width, size.Height) } };
            }
        }

        return (output, usable);
    }

    /// <summary>
    /// The next pen step (the animals family's containment Need); unknown while any planning fact is unread, and a
    /// no-op step with no animal or no planned pen. A pen is built from fence, gate and marker: the ward's bed does not
    /// gate it.
    /// </summary>
    internal static (HerdStep Step, bool Known) PenStepOf(ColonyProjection facts)
    {
        var ak = facts.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!ak || !pk || !ck || !census!.Colony)
        {
            return (default!, false);
        }

        if (animals!.Count == 0 || plan!.RoomsOf(PlannedRole.Pen).Count == 0)
        {
            return (new HerdStep(), true);
        }

        var (furniture, _) = HerdFurnitureOf(facts);
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        if (!gk)
        {
            return (default!, false);
        }

        return (HerdRoomsRules.NextPenStep(plan, plan.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census.Buildings, furniture), true);
    }

    /// <summary>
    /// The next hospital ward step (the animals family's ward Need); unknown while any planning fact is unread or the
    /// animal bed is unusable, and a no-op step with no ward demand or no planned hospital.
    /// </summary>
    internal static (HerdStep Step, bool Known) WardStepOf(ColonyProjection facts)
    {
        var ak = facts.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!ak || !pk || !rk || !ck || !sk || !census!.Colony)
        {
            return (default!, false);
        }

        var ward = HerdWardRules.WardBeds(animals!, facts.Facts.HerdPolicy());
        if (animals!.Count == 0 || ward == 0 || plan!.RoomsOf(PlannedRole.Hospital).Count == 0)
        {
            return (new HerdStep(), true);
        }

        var (furniture, usable) = HerdFurnitureOf(facts);
        if (!usable)
        {
            return (default!, false);
        }

        return (HerdRoomsRules.NextWardStep(plan, rooms!, census.Buildings, sleeping!.Beds, ward, furniture), true);
    }

    /// <summary>Whether the hospital ward is ready; a ward the frame cannot show standing (plan, rooms, construction or sleeping census unread) is not.</summary>
    internal static bool WardReadyOf(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!pk || !rk || !ck || !sk || !census!.Colony)
        {
            return false;
        }

        return HerdWardRules.WardReady(plan!, rooms!, census.Buildings, sleeping!.Beds, facts.Shapes.Furniture.AnimalBed);
    }
}
