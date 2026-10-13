using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

// Go: rounds_building_selection.go. The RoundsBuildingPlanner type itself (fields reviewer, native, concern,
// definition, environment, the per-concern proposals) is declared by the sibling slice that ports rounds_building.go.
public sealed partial class RoundsBuildingPlanner
{
    internal static bool PendingFacility(Progress progress, string definition) =>
        progress.Action.Building is { } building && building.Definition == definition && PendingWork(progress);

    public static RoundsBuildingPlanner NewRoundsCookingPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsCookingPlanner: reviewer == nil || native == nil");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyApi.EnsureCooking, Definition = "Campfire", Environment = PlacementEnvironment.Anywhere };
    }

    public static RoundsBuildingPlanner NewRoundsButcherPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsButcherPlanner: reviewer == nil || native == nil");
        }
        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsButcherPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyApi.EnsureFoodSupply, Definition = "TableButcher", Environment = PlacementEnvironment.Anywhere };
    }

    /// <summary>The count the concern wants built, the method that builds it, or the verdict that explains why none is wanted.</summary>
    internal (long Count, MethodId Method, Verdict Verdict) Selection(ColonyProjection facts)
    {
        if (!facts.Facts.Colonists.TryGetValue(out var count) || count <= 0)
        {
            return (0, default, FieldUnavailable("colonists"));
        }
        var c = Concern;
        if (c == PolicyApi.EnsureFoodSupply)
        {
            if (Definition != "TableButcher")
            {
                return (0, default, FieldUnavailable("butcher_table_definition"));
            }
            if (!facts.ButcheringBenches.TryGetValue(out var benches))
            {
                return (0, default, FieldUnavailable("butchering_benches"));
            }
            if (!ButcherBenchApart(benches, facts.Rooms))
            {
                return (1, new MethodId("butcher-spot"), default);
            }
            if (StandingButcherSpot(facts).Retire)
            {
                return (1, new MethodId("butcher-spot-retire"), default);
            }
            if (ButcherTableWanted(facts, benches))
            {
                return (1, new MethodId("butcher-table"), default);
            }
            return (0, default, BuildingReasonExistingFacility);
        }
        if (c == PolicyApi.EnsureTemperatureSafety)
        {
            if (Temperature is null)
            {
                return (0, default, FieldUnavailable("temperature_proposal"));
            }
            return (1, Temperature.Key, default);
        }
        if (c == PolicyApi.MaintainRefrigeration)
        {
            if (Refrigeration is null || Refrigeration.Method != RefrigerationMethod.Build)
            {
                return (0, default, FieldUnavailable("refrigeration_proposal"));
            }
            return (1, Refrigeration.Key, default);
        }
        if (c == PolicyApi.MaintainLighting)
        {
            if (Lighting is null || Lighting.Method != LightingMethod.Build)
            {
                return (0, default, FieldUnavailable("lighting_proposal"));
            }
            return (1, Lighting.Key, default);
        }
        if (c == PolicyApi.MaintainFlooring)
        {
            if (Flooring is null || Flooring.Method != FlooringMethod.Build)
            {
                return (0, default, FieldUnavailable("flooring_proposal"));
            }
            return (Flooring.Cells.Count, Flooring.Key, default);
        }
        if (c == PolicyApi.MaintainRoutes)
        {
            if (Routes is null || Routes.Method != RoutesMethod.Build)
            {
                return (0, default, FieldUnavailable("routes_proposal"));
            }
            return (1, Routes.Key, default);
        }
        if (c == PolicyApi.EnsureBasicPower)
        {
            if (Power is null)
            {
                return (0, default, FieldUnavailable("power_proposal"));
            }
            if (Power.Method == PowerMethod.Connect)
            {
                return (Power.Cells.Count, Power.Key, default);
            }
            if (Power.Method == PowerMethod.Generate || Power.Method == PowerMethod.Store)
            {
                return (1, Power.Key, default);
            }
            if (Power.Method == PowerMethod.Shelter)
            {
                return (2L * Power.Room.Width + 2L * Power.Room.Height - 4, Power.Key, default);
            }
            return (0, default, FieldUnavailable("power_proposal"));
        }
        if (c == PolicyApi.EnsureComfort)
        {
            if (Phase == Phase.ComfortSpot)
            {
                return (1, PartySpotPlaceMethod(PartySpotRoom), default);
            }
            if (Phase == Phase.ComfortBasic)
            {
                return (1, new MethodId("basic-comfort-" + Definition), default);
            }
            return (1, new MethodId("comfort-" + Definition), default);
        }
        if (c == PolicyApi.MaintainResource || c == PolicyApi.MaintainEquipment)
        {
            if (CraftingSpot)
            {
                if (StandingCraftingSpot(facts).Retire)
                {
                    return (1, new MethodId("crafting-spot-retire"), default);
                }
                return (1, new MethodId("crafting-spot"), default);
            }
            if (Workshop is not null && Workshop.Ordinal > 0)
            {
                return (1, new MethodId(string.Create(CultureInfo.InvariantCulture, $"workshop-{Definition}-{Workshop.Ordinal + 1}")), default);
            }
            return (1, new MethodId("workshop-" + Definition), default);
        }
        if (c == PolicyApi.MaintainMedicalReserves)
        {
            return (1, new MethodId("hospital-" + Definition), default);
        }
        if (c == PolicyApi.EnsureResearch)
        {
            return (1, new MethodId("laboratory-" + Definition), default);
        }
        if (c == PolicyApi.MaintainHousing)
        {
            if (Phase == Phase.HousingSleeping)
            {
                if (Sleeping is null)
                {
                    return (0, default, FieldUnavailable("sleeping_proposal"));
                }
                return (1, new MethodId(string.Create(CultureInfo.InvariantCulture, $"sleeping-{Definition}-{Sleeping.Unhoused}")), default);
            }
            if (!facts.Facts.IndoorCapacity.TryGetValue(out var capacity))
            {
                return (0, default, FieldUnavailable("indoor_capacity"));
            }
            var prefix = "";
            if (Phase == Phase.HousingExpansion)
            {
                if (facts.Facts.FoodPlan.TryGetValue(out var foodPlan) && foodPlan.GapPerDay > 0)
                {
                    return (0, default, AwaitingFoodPlan("housing_expansion"));
                }
                if (count >= long.MaxValue)
                {
                    return (0, default, FieldUnavailable("colonist_count"));
                }
                count++;
                prefix = "expansion-";
            }
            else if (facts.Facts.HousingTarget.TryGetValue(out var target))
            {
                count = Math.Max(count, target);
            }
            var missing = count - capacity;
            if (missing <= 0)
            {
                return (0, default, BuildingReasonNoDeficit);
            }
            if (missing > 64)
            {
                return (0, default, NoSpace("housing_bound"));
            }
            if (Shelter)
            {
                return (missing, default, default);
            }
            return (missing, new MethodId(string.Create(CultureInfo.InvariantCulture, $"{prefix}indoor-sleeping-{count}-{missing}")), default);
        }
        if (c == PolicyApi.EnsureCooking)
        {
            if (Paste.Count > 0)
            {
                return (Paste.Count, new MethodId("nutrient-paste"), default);
            }
            if (!FoodPlanSupportOf(facts.Facts.FoodPlan, CandidateKind.Cook, "cooking-capacity"))
            {
                return (0, default, AwaitingFoodPlan("cooking-capacity"));
            }
            if (!facts.Facts.Cooking.TryGetValue(out var ready))
            {
                return (0, default, FieldUnavailable("cooking"));
            }
            if (ready)
            {
                return (0, default, BuildingReasonNoDeficit);
            }
            if (!facts.CookingBenches.TryGetValue(out var cooking))
            {
                return (0, default, FieldUnavailable("cooking_benches"));
            }
            var unknown = false;
            foreach (var bench in cooking)
            {
                var known = bench.Usable.TryGetValue(out var usable);
                if (bench.Definition == "Campfire" || (known && usable))
                {
                    return (0, default, BuildingReasonExistingFacility);
                }
                unknown = unknown || !known;
            }
            if (unknown)
            {
                return (0, default, FieldUnavailable("cooking_bench_bills"));
            }
            if (CampfireIntentStanding(facts.Facts.ConstructionClaims, facts.Facts.CurrentConstruction))
            {
                return (0, default, BuildingReasonExistingFacility);
            }
            return (1, new MethodId("campfire"), default);
        }
        return (0, default, FieldUnavailable("concern_selection"));
    }

    internal bool ButcherFacility() => Concern == PolicyApi.EnsureFoodSupply;

    internal bool WorkshopLadder() => (Concern == PolicyApi.MaintainResource || Concern == PolicyApi.MaintainEquipment) && !CraftingSpot;
}

internal static partial class BuildingRuntimePackage
{
    internal static bool ButchersAllColocated(IReadOnlyList<CookingBench> benches, Fact<RoomObservation> rooms)
    {
        if (!PolicyApi.KitchenSeparation(rooms).TryGetValue(out var shared) || benches.Count == 0)
        {
            return false;
        }
        var colocated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in shared)
        {
            colocated.Add(room.Id);
        }
        foreach (var bench in benches)
        {
            if (!bench.Room.TryGetValue(out var room) || !colocated.Contains(room))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a campfire intent already stands: an unheated campfire in the census or an open campfire claim.</summary>
    internal static bool CampfireIntentStanding(Fact<EquatableArray<ConstructionClaim>> claims, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return false;
        }
        claims.TryGetValue(out var history);
        var heat = HeatCampfireCells(history);
        foreach (var b in census.Buildings)
        {
            if (b.Building is { Definition: "Campfire" } && b.Cells.Count > 0 && !heat.Contains(b.Cells[0]))
            {
                return true;
            }
        }
        foreach (var claim in history)
        {
            if (claim.Building is { Definition: "Campfire" } campfire && PolicyApi.WorkOpen(campfire, observed) == BuildingWork.BuildingOpen)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ButcherTableWanted(ColonyProjection facts, IReadOnlyList<CookingBench> benches)
    {
        foreach (var b in benches)
        {
            if (b.Definition == "TableButcher" && !ButchersAllColocated([b], facts.Rooms))
            {
                return false;
            }
        }
        return facts.Facts.Wood.TryGetValue(out var wood) && wood >= PolicyApi.ButcherTableWood && ComfortBuilderAvailable(facts, "TableButcher");
    }

    internal static bool ButcherBenchApart(IReadOnlyList<CookingBench> benches, Fact<RoomObservation> rooms) =>
        benches.Count > 0 && !ButchersAllColocated(benches, rooms);

    internal static bool ButcherRoomWanted(ColonyProjection facts, IReadOnlyList<CookingBench> benches) =>
        !ButcherBenchApart(benches, facts.Rooms) || ButcherTableWanted(facts, benches);

    internal static (CurrentBuilding Building, bool Retire) StandingButcherSpot(ColonyProjection facts)
    {
        if (!facts.ButcheringBenches.TryGetValue(out var benches) || !facts.Facts.CurrentConstruction.TryGetValue(out var census))
        {
            return (new CurrentBuilding(), false);
        }
        var table = false;
        foreach (var bench in benches)
        {
            table = table || bench.Definition == PolicyConstants.ButcherTableDefinition;
        }
        if (!table)
        {
            return (new CurrentBuilding(), false);
        }
        foreach (var bench in benches)
        {
            if (bench.Definition != PolicyApi.ButcherSpotDefinition)
            {
                continue;
            }
            foreach (var b in census.Buildings)
            {
                if (b.Id == bench.Id && b.Cells.Count > 0)
                {
                    return (b, true);
                }
            }
        }
        return (new CurrentBuilding(), false);
    }

    internal static bool ButcherBenchDefinition(string definition) =>
        definition == PolicyConstants.ButcherTableDefinition || definition == PolicyApi.ButcherSpotDefinition;

    internal static (CurrentBuilding Building, bool Retire) StandingCraftingSpot(ColonyProjection facts)
    {
        if (!facts.Facts.CurrentConstruction.TryGetValue(out var census) || !PositiveFact(facts.Facts.CraftingSpotRetireOwed))
        {
            return (new CurrentBuilding(), false);
        }
        foreach (var b in census.Buildings)
        {
            if (b.Building?.Definition == PolicyApi.CraftingSpotDefinition && b.Cells.Count > 0)
            {
                return (b, true);
            }
        }
        return (new CurrentBuilding(), false);
    }
}
