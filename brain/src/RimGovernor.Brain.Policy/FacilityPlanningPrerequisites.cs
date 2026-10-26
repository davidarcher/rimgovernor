using System.Globalization;

namespace RimGovernor.Brain.Policy;

public enum IndoorSleepingMethod { NoDeficit, Bound, Build }
public enum WorkshopPrerequisite { Product, Barrel, Wort }

public static partial class FacilityPlanningRules
{
    public static FacilityEligibility DefinitionAvailability(PlanningDefinition def) => def.Available ? default
        : def.Research.Count == 0 ? new(Cause.AwaitingPlan, def.Name, "unavailable")
        : new(Cause.AwaitingPlan, "research", string.Join("+", def.Research));

    public static FacilityEligibility DefinitionsGate(IReadOnlyList<PlanningDefinition> definitions, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var def = definitions.FirstOrDefault(d => d.Name == name);
            if (def is null) return new(Cause.FieldUnavailable, name + "_definition");
            var availability = DefinitionAvailability(def);
            if (availability.Cause != Cause.None) return availability;
            if (def.ConstructionSkill != 0)
                return new(Cause.NoWorker, "builder_for_" + name, string.Create(CultureInfo.InvariantCulture, $"construction_skill_{def.ConstructionSkill}"));
        }
        return default;
    }

    public static (IndoorSleepingMethod Method, long Count) IndoorSleepingDemand(long count, long capacity)
    {
        var missing = count - capacity;
        return missing <= 0 ? (IndoorSleepingMethod.NoDeficit, 0)
            : missing > 64 ? (IndoorSleepingMethod.Bound, 0) : (IndoorSleepingMethod.Build, missing);
    }

    public static FacilityEligibility ShellDefinitionsGate(PlanningDefinition? wall, PlanningDefinition? door) =>
        wall is null || door is null ? new(Cause.FieldUnavailable, "wall_door_definitions")
        : !wall.Available || !door.Available ? new(Cause.FieldUnavailable, "wall_door_availability") : default;

    public static bool UnstockedUntilKitAllowed(ReviewFacts facts, params string[] stuffs)
    {
        var stock = ResourceStockRules.Count(facts.Census.Resources);
        if (!facts.EventLoot.Any(row => row.IsSafe && row.Forbidden)) return false;
        return stuffs.Any(stuff => stuff != "" && stock.GetValueOrDefault(new Resource(stuff)) <= 0);
    }

    public static bool SuiteStockAvailable(ReviewFacts facts, PlannedRoom room)
    {
        if (room.Role != PlannedRole.Suite) return true;
        var inside = room.Region.Bounds();
        var walls = ColonyPlanningRules.StockedStuff(facts, ResourceStockRules.Count(facts.Census.Resources), ContainmentCellRules.ShellWallDefinition)?.Count ?? 0;
        return walls >= 2 * (inside.Width + inside.Height) + 4;
    }

    public static Rotation VentRotation(LayoutPlan plan, Cell cell)
    {
        foreach (var site in plan.LiveSites(NodeKind.Vent))
            if (site.Origin == cell && site.Rotation is Heading.Toward toward) return toward.Side;
        return Rotation.North;
    }

    public static WorkshopPrerequisite WorkshopPrerequisiteOf(Resource resource, long barrels, string beerDefinition) =>
        resource.ToString() != beerDefinition ? WorkshopPrerequisite.Product
        : barrels == 0 ? WorkshopPrerequisite.Barrel : WorkshopPrerequisite.Wort;
}
