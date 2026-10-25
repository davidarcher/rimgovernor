using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>A building prerequisite failure; a zero cause means eligible.</summary>
public readonly record struct FacilityEligibility(Cause Cause, string Subject = "", string Detail = "");

/// <summary>The existing facility ladder's selected action, or the reason it has no action.</summary>
public enum FacilityDemand { None, Existing, ButcherSpot, RetireButcherSpot, ButcherTable, Campfire, AwaitingFoodPlan }

public static partial class FacilityPlanningRules
{
    public static FacilityEligibility BuilderGate(ReviewFacts facts, string definition)
    {
        var def = facts.Definitions.FirstOrDefault(d => d.Name == definition);
        if (def is null) return new(Cause.FieldUnavailable, definition + "_definition");
        if (!def.Available)
        {
            return def.Research.Count == 0
                ? new(Cause.AwaitingPlan, def.Name, "unavailable")
                : new(Cause.AwaitingPlan, "research", string.Join("+", def.Research));
        }
        var minimum = def.ConstructionSkill;
        if (minimum < 0) return new(Cause.FieldUnavailable, definition + "_construction_skill");
        if (BuilderAvailable(facts.ForecastObserved.WorkPawns.Pawns, minimum)) return default;
        var need = minimum > 0 ? string.Create(CultureInfo.InvariantCulture, $"construction_skill_{minimum}") : "construction_work_enabled";
        return new(Cause.NoWorker, "builder_for_" + definition, need);
    }

    public static FacilityDemand ButcherDemand(ReviewFacts facts)
    {
        var benches = facts.EssentialInputs.Benches.Where(b => b.Butcher).ToArray();
        if (benches.Length == 0) return FacilityDemand.ButcherSpot;
        if (StandingButcherSpot(facts) is not null) return FacilityDemand.RetireButcherSpot;
        if (!benches.Any(b => b.Definition == KitchenSeparationRules.ButcherTableDefinition)
            && facts.ButcherTableAffordable && BuilderGate(facts, KitchenSeparationRules.ButcherTableDefinition).Cause == Cause.None)
            return FacilityDemand.ButcherTable;
        return FacilityDemand.Existing;
    }

    public static FacilityDemand CookingDemand(ReviewFacts facts, string campfireDefinition)
    {
        if (facts.FoodPlan is not { } support || !FoodOrdersRules.FoodPlanSupport(support, CandidateKind.Cook, "cooking-capacity"))
            return FacilityDemand.AwaitingFoodPlan;
        if (facts.Cooking) return FacilityDemand.None;
        if (facts.EssentialInputs.Benches.Any(b => !b.Butcher && (b.Definition == campfireDefinition || b.Usable))
            || CampfireIntentStanding(facts.ConstructionClaims, facts.CurrentConstruction, campfireDefinition))
            return FacilityDemand.Existing;
        return FacilityDemand.Campfire;
    }

    public static ComfortMethod BasicComfortMethod(ReviewFacts facts, IReadOnlyList<SiteCell>? cells)
    {
        var v = facts.RequireBasicComfort();
        var review = BasicComfortRules.ReviewBasicComfort(v);
        var method = BasicComfortRules.SelectBasicComfortMethod(v, review);
        if (method == ComfortMethod.NoMethod && review.MissingVariety > 0)
        {
            method = RecreationRules.SelectRecreationVariety(v, m =>
                (m.PowerW <= 0 || (cells ?? throw new RequiredFactException("planning window")).Any(c => c.Roofed && c.Indoors && !c.Occupied() && PoweredRecreationCell(facts, c.Cell, m.PowerW)))
                && BuilderGate(facts, m.Definition).Cause == Cause.None);
        }
        return method;
    }
}
