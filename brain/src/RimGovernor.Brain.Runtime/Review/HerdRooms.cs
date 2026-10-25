using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class HerdRooms
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
    internal static (HerdFurniture Furniture, bool Usable) HerdFurnitureOf(ColonyReading facts)
    {
        var output = new HerdFurniture();
        var usable = true;
        foreach (var name in new[] { facts.Facts.Shapes.Furniture.AnimalBed })
        {
            var d = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, name);
            if (d is null)
            {
                throw new ControlException($"herdFurniture: {name} was not read from the native catalog");
            }

            var size = d.Size;
            var available = d.Available;

            if (!available || size.Width < 1 || size.Height < 1)
            {
                usable = false;
                continue;
            }

            output = output with { Bed = new InteriorPieceDef { Def = name, Size = new Cell(size.Width, size.Height) } };
        }

        var marker = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, HerdPenRules.PenMarkerDefinition);
        if (marker is not null)
        {
            var size = marker.Size;
            var available = marker.Available;
            if (available && size.Width >= 1 && size.Height >= 1)
            {
                output = output with { Marker = new InteriorPieceDef { Def = HerdPenRules.PenMarkerDefinition, Size = new Cell(size.Width, size.Height) } };
            }
        }

        return (output, usable);
    }

    /// <summary>
    /// The next pen step (the animals family's containment Need); missing planning inputs fail the read, and a
    /// no-op step with no animal or no planned pen. A pen is built from fence, gate and marker: the ward's bed does not
    /// gate it.
    /// </summary>
    internal static HerdStep PenStepOf(ColonyReading facts)
    {
        var animals = facts.Facts.AnimalUpkeep.Animals;
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var census = facts.Facts.CurrentConstruction;
        if (!pk || census is null)
        {
            throw new RequiredFactException("herd room planning census");
        }

        if (animals.Count == 0 || plan!.RoomsOf(PlannedRole.Pen).Count == 0)
        {
            return new HerdStep();
        }

        var (furniture, _) = HerdFurnitureOf(facts);
        var ground = BuildingPlanner.ColonyGround(facts);

        return HerdRoomsRules.NextPenStep(plan, plan.GroundWithRock(ground, BuildingPlanner.NaturalRock(facts)), census.Buildings, furniture);
    }

    /// <summary>
    /// The next hospital ward step (the animals family's ward Need); planning inputs are required. An unavailable
    /// animal bed has a named step; no ward demand or no planned hospital has a no-op step.
    /// </summary>
    internal static HerdStep WardStepOf(ColonyReading facts)
    {
        var animals = facts.Facts.AnimalUpkeep.Animals;
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        var sleeping = facts.Facts.Sleeping;
        if (!pk || census is null)
        {
            throw new RequiredFactException("herd room planning census");
        }

        var ward = HerdWardRules.WardBeds(animals, facts.Facts.HerdPolicy());
        if (animals.Count == 0 || ward == 0 || plan!.RoomsOf(PlannedRole.Hospital).Count == 0)
        {
            return new HerdStep();
        }

        var (furniture, usable) = HerdFurnitureOf(facts);
        if (!usable)
        {
            return new HerdStep { Kind = HerdStepKind.BedUnavailable };
        }

        return HerdRoomsRules.NextWardStep(plan, rooms ?? throw new RequiredFactException("herd room census"), census.Buildings, (sleeping ?? throw new RequiredFactException("herd sleeping census")).Beds, ward, furniture);
    }

    /// <summary>Whether the observed hospital ward is ready; every planning census is required.</summary>
    internal static bool WardReadyOf(ColonyReading facts)
    {
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        var sleeping = facts.Facts.Sleeping;
        if (!pk || census is null)
        {
            throw new RequiredFactException("herd room planning census");
        }

        return HerdWardRules.WardReady(plan!, rooms ?? throw new RequiredFactException("herd room census"), census.Buildings, (sleeping ?? throw new RequiredFactException("herd sleeping census")).Beds, facts.Facts.Shapes.Furniture.AnimalBed);
    }
}
