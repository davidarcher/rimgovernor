using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>A building prerequisite failure; a zero cause means eligible.</summary>
public readonly record struct FacilityEligibility(Cause Cause, string Subject = "", string Detail = "");

/// <summary>A food facility's state before the shared layout comparison hosts its load: nothing owed, a bench standing, the food plan unread, or a load the comparison is to host.</summary>
public enum FacilityDemand { None, Existing, AwaitingFoodPlan, Unhosted }

public static partial class FacilityPlanningRules
{
    public static FacilityEligibility BuilderGate(ReviewFacts facts, string definition)
    {
        var def = facts.Definitions.FirstOrDefault(d => d.Name == definition);
        if (def is null) return new(Cause.FieldUnavailable, definition + "_definition");
        var availability = DefinitionAvailability(def);
        if (availability.Cause != Cause.None) return availability;
        var minimum = def.ConstructionSkill;
        if (minimum < 0) return new(Cause.FieldUnavailable, definition + "_construction_skill");
        if (BuilderAvailable(facts.ForecastObserved.WorkPawns.Pawns, minimum)) return default;
        var need = minimum > 0 ? string.Create(CultureInfo.InvariantCulture, $"construction_skill_{minimum}") : "construction_work_enabled";
        return new(Cause.NoWorker, "builder_for_" + definition, need);
    }

    /// <summary>
    /// The food/supply Need's butcher bench, before the shared layout comparison hosts its load: a standing butcher bench is
    /// the facility; with none, the butchering load is unhosted and the comparison chooses spot, table or deferral by their
    /// consequences (RG-HABITAT-005). No definition is chosen here.
    /// </summary>
    public static FacilityDemand ButcherDemand(ReviewFacts facts) =>
        facts.EssentialInputs.Benches.Any(b => b.Butcher) ? FacilityDemand.Existing : FacilityDemand.Unhosted;

    /// <summary>
    /// The cooking Need's bench, before the shared layout comparison hosts its load: the food plan must support cooking
    /// capacity, a colony already cooking owes nothing, a standing cooking bench (a campfire counts while it stands) is the
    /// facility; else the cooking load is unhosted and the comparison chooses campfire, stove or deferral (RG-HABITAT-005).
    /// </summary>
    public static FacilityDemand CookingDemand(ReviewFacts facts, string campfireDefinition)
    {
        if (facts.FoodPlan is not { } support || !FoodOrdersRules.FoodPlanSupport(support, CandidateKind.Cook, "cooking-capacity"))
            return FacilityDemand.AwaitingFoodPlan;
        if (facts.Cooking) return FacilityDemand.None;
        if (facts.EssentialInputs.Benches.Any(b => !b.Butcher && (b.Definition == campfireDefinition || b.Usable)))
            return FacilityDemand.Existing;
        return FacilityDemand.Unhosted;
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
