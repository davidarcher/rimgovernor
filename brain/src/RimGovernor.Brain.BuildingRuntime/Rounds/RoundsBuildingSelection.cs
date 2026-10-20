using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// The RoundsBuildingPlanner type itself (fields reviewer, native, concern,
// definition, environment, the per-concern proposals) is declared by its sibling partial files.
public sealed partial class RoundsBuildingPlanner
{
    internal static bool PendingFacility(Progress progress, string definition) =>
        progress.Action.Building is { } building && building.Definition == definition && RoundsPendingWork.PendingWork(progress);

    public static RoundsBuildingPlanner NewRoundsCookingPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsCookingPlanner: reviewer == nil || native == nil");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = FoodRules.CookingNeed.FilingKey, Need = FoodRules.CookingNeed, Definition = "Campfire", Environment = PlacementEnvironment.Anywhere };
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
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = FoodRules.FoodSupplyNeed.FilingKey, Need = FoodRules.FoodSupplyNeed, Definition = "TableButcher", Environment = PlacementEnvironment.Anywhere };
    }

    /// <summary>The count the concern wants built, the method that builds it, or the verdict that explains why none is wanted.</summary>
    internal (long Count, MethodId Method, Verdict Verdict) Selection(ColonyProjection facts)
    {
        if (!facts.Facts.Colonists.TryGetValue(out var count) || count <= 0)
        {
            return (0, default, FieldUnavailable("colonists"));
        }
        var c = Concern;
        if (c == FoodRules.FoodSupplyNeed.FilingKey)
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
        if (c == ShelterFamily.Temperature.FilingKey)
        {
            if (Temperature is null)
            {
                return (0, default, FieldUnavailable("temperature_proposal"));
            }
            return (1, Temperature.Key, default);
        }
        if (c == RoomsFamily.Lighting.FilingKey)
        {
            if (Lighting is null || Lighting.Method != LightingMethod.Build)
            {
                return (0, default, FieldUnavailable("lighting_proposal"));
            }
            return (1, Lighting.Key, default);
        }
        if (c == RoomsFamily.Flooring.FilingKey)
        {
            if (Flooring is null || Flooring.Method != FlooringMethod.Build)
            {
                return (0, default, FieldUnavailable("flooring_proposal"));
            }
            return (Flooring.Cells.Count, Flooring.Key, default);
        }
        if (c == LayoutFamily.Routes.FilingKey)
        {
            if (Routes is null || Routes.Method != RoutesMethod.Build)
            {
                return (0, default, FieldUnavailable("routes_proposal"));
            }
            return (1, Routes.Key, default);
        }
        if (c == PowerFamily.Basic.FilingKey)
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
        if (RoomsFamily.ComfortStep(c))
        {
            if (Phase == Phase.ComfortSpot)
            {
                return (1, PartySpotPlaceMethod(PartySpotRoom), default);
            }
            if (Phase == Phase.ComfortWorkChair)
            {
                if (Cells is not [var seat])
                {
                    return (0, default, FieldUnavailable("work_chair_seat"));
                }
                return (1, new MethodId(string.Create(CultureInfo.InvariantCulture, $"work-chair-{Definition}-{seat.X}-{seat.Z}")), default);
            }
            if (Phase == Phase.ComfortBasic)
            {
                return (1, new MethodId("basic-comfort-" + Definition), default);
            }
            return (1, new MethodId("comfort-" + Definition), default);
        }
        if (c == EconomyFamily.Resource.FilingKey || c == MilitaryFamily.Equipment.FilingKey)
        {
            if (Workshop is not null && Workshop.Ordinal > 0)
            {
                return (1, new MethodId(string.Create(CultureInfo.InvariantCulture, $"workshop-{Definition}-{Workshop.Ordinal + 1}")), default);
            }
            return (1, new MethodId("workshop-" + Definition), default);
        }
        if (c == MedicalRules.MedicalCareNeed.FilingKey)
        {
            return (1, new MethodId("hospital-" + Definition), default);
        }
        if (c == EconomyFamily.Research.FilingKey)
        {
            return (1, new MethodId("laboratory-" + Definition), default);
        }
        if (ShelterFamily.Housing(c))
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
        if (c == FoodRules.CookingNeed.FilingKey)
        {
            if (Paste.Count > 0)
            {
                return (Paste.Count, new MethodId("nutrient-paste"), default);
            }
            if (!Rounder.FoodPlanSupportOf(facts.Facts.FoodPlan, CandidateKind.Cook, "cooking-capacity"))
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

    internal bool ButcherFacility() => Concern == FoodRules.FoodSupplyNeed.FilingKey;

    internal bool WorkshopLadder() => Concern == EconomyFamily.Resource.FilingKey || GearLadder();
}

partial class RoundsBuildingPlanner
{
    internal static bool ButchersAllColocated(IReadOnlyList<CookingBench> benches, Fact<RoomObservation> rooms)
    {
        if (!KitchenSeparationRules.KitchenSeparation(rooms).TryGetValue(out var shared) || benches.Count == 0)
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
            if (claim.Building is { Definition: "Campfire" } campfire && ConstructionOwnershipRules.WorkOpen(campfire, observed) == BuildingWork.BuildingOpen)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the stock covers the butcher table's build cost at its cheapest stuff choice; unknown without the definition or the stock.</summary>
    internal static Fact<bool> ButcherTableAffordable(ColonyProjection facts)
    {
        if (!facts.Resources.TryGetValue(out var stock))
        {
            return Unknown<bool>();
        }
        foreach (var d in facts.Definitions)
        {
            if (d.Name != KitchenSeparationRules.ButcherTableDefinition)
            {
                continue;
            }
            var (price, ok) = RoundsDefenseLayoutPlanner.DefenseUnitPrice(facts, d);
            if (!ok)
            {
                return Unknown<bool>();
            }
            return Known(price.Costs.All(a => stock.GetValueOrDefault(a.Resource) >= a.Count));
        }
        return Unknown<bool>();
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
        return facts.Facts.ButcherTableAffordable.TryGetValue(out var affordable) && affordable && ComfortBuilderAvailable(facts, "TableButcher");
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
            table = table || bench.Definition == KitchenSeparationRules.ButcherTableDefinition;
        }
        if (!table)
        {
            return (new CurrentBuilding(), false);
        }
        foreach (var bench in benches)
        {
            if (bench.Definition != RoundsRules.ButcherSpotDefinition)
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
        definition == KitchenSeparationRules.ButcherTableDefinition || definition == RoundsRules.ButcherSpotDefinition;
}
