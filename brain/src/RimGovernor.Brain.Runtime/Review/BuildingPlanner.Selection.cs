using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

// The BuildingPlanner type itself (fields reviewer, native, concern,
// definition, environment, the per-concern proposals) is declared by its sibling partial files.
public sealed partial class BuildingPlanner
{
    internal static bool PendingFacility(Progress progress, string definition) =>
        progress.Action.Building is { } building && building.Definition == definition && PendingWork.IsPending(progress);

    public static BuildingPlanner NewReviewCookingPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewCookingPlanner: reviewer == nil || native == nil");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = FoodRules.CookingNeed.FilingKey, Need = FoodRules.CookingNeed, Definition = "Campfire", Environment = PlacementEnvironment.Anywhere };
    }

    public static BuildingPlanner NewReviewButcherPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewButcherPlanner: reviewer == nil || native == nil");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewButcherPlanner: !ok");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = FoodRules.FoodSupplyNeed.FilingKey, Need = FoodRules.FoodSupplyNeed, Definition = "TableButcher", Environment = PlacementEnvironment.Anywhere };
    }

    /// <summary>The count the concern wants built, the method that builds it, or the verdict that explains why none is wanted.</summary>
    internal (long Count, MethodId Method, Verdict Verdict) Selection(ColonyReading facts)
    {
        var count = facts.Facts.Census.ColonistCount;
        if (count <= 0)
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
            return FacilityPlanningRules.ButcherDemand(facts.Facts) switch
            {
                FacilityDemand.ButcherSpot => (1, new MethodId("butcher-spot"), default),
                FacilityDemand.RetireButcherSpot => (1, new MethodId("butcher-spot-retire"), default),
                FacilityDemand.ButcherTable => (1, new MethodId("butcher-table"), default),
                FacilityDemand.Existing => (0, default, BuildingReasonExistingFacility),
                var unexpected => throw new ControlException($"unexpected butcher demand: {unexpected}"),
            };
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
        if (c == ShelterFamily.Sleeping.FilingKey)
        {
            if (Phase == Phase.HousingSleeping)
            {
                if (Sleeping is null)
                {
                    return (0, default, FieldUnavailable("sleeping_proposal"));
                }
                return (1, new MethodId(string.Create(CultureInfo.InvariantCulture, $"sleeping-{Definition}-{Sleeping.Unhoused}")), default);
            }
            var capacity = facts.Facts.Census.IndoorSleepingCapacity;
            var missing = count - capacity;
            if (missing <= 0)
            {
                return (0, default, BuildingReasonNoDeficit);
            }
            if (missing > 64)
            {
                return (0, default, NoSpace("housing_bound"));
            }
            return (missing, new MethodId(string.Create(CultureInfo.InvariantCulture, $"indoor-sleeping-{count}-{missing}")), default);
        }
        if (c == FoodRules.CookingNeed.FilingKey)
        {
            if (Paste.Count > 0)
            {
                return (Paste.Count, new MethodId("nutrient-paste"), default);
            }
            return FacilityPlanningRules.CookingDemand(facts.Facts, nameof(RimWorld.ThingDefOf.Campfire)) switch
            {
                FacilityDemand.AwaitingFoodPlan => (0, default, AwaitingFoodPlan("cooking-capacity")),
                FacilityDemand.None => (0, default, BuildingReasonNoDeficit),
                FacilityDemand.Existing => (0, default, BuildingReasonExistingFacility),
                FacilityDemand.Campfire => (1, new MethodId("campfire"), default),
                var unexpected => throw new ControlException($"unexpected cooking demand: {unexpected}"),
            };
        }
        return (0, default, FieldUnavailable("concern_selection"));
    }

    internal bool ButcherFacility() => Concern == FoodRules.FoodSupplyNeed.FilingKey;

    internal bool WorkshopLadder() => Concern == EconomyFamily.Resource.FilingKey || GearLadder();
}
