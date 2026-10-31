using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

/// <summary>What makes an exact pawn order meaningful now, read from current observations of the work the order serves.
/// A forecasted benefit is never a premise: the comparison's ranking says how much better, the premise says why act at all.</summary>
public abstract record InterventionPremise
{
    private InterventionPremise() { }

    /// <summary>A current observed deficit the dispatched work serves: the pawn's need at or below its native threshold while no
    /// native service is captured for it.</summary>
    public sealed record ObservedShortfall(string Need, string Pawn, string Evidence) : InterventionPremise;

    /// <summary>The food owner's observed supply, reserve and recovery evidence (<see cref="NativeFoodAssessment.Reviewed"/>) shows a
    /// risk before anyone is harmed, and the dispatched work produces or moves food.</summary>
    public sealed record CredibleRisk(NativeFoodRisk Risk, double RunwayDays, FoodRecoveryTiming Timing) : InterventionPremise;

    /// <summary>An assigned capability requirement (<see cref="AssignedCapability"/>) the dispatched practice trains: unmet by the
    /// observed skill level now, or lapsing within the horizon under the observed skill decay.</summary>
    public sealed record CapabilityOpportunity(string Requirement, string Pawn, string Skill, CapabilityGap Gap) : InterventionPremise;

    /// <summary>The dispatched work serves something whose premise evidence is missing: neither a premise nor its absence is established.</summary>
    public sealed record Unassessed(string Reason) : InterventionPremise;

    /// <summary>No observation the dispatched work serves makes the order meaningful.</summary>
    public sealed record None : InterventionPremise;
}

public enum CapabilityGap { UnmetNow, LapsesWithinHorizon }

/// <summary>Whether continuing native operation, with its settings and work creation, answers the premise in time.</summary>
public abstract record NativeResponse
{
    private NativeResponse() { }

    /// <summary>A supported settings/work-creation response arrives in time; <see cref="Basis"/> names the evidence.</summary>
    public sealed record Supported(string Basis) : NativeResponse;

    /// <summary>The supported response is known to arrive too late; settings need not be tried and allowed to fail first.</summary>
    public sealed record KnownTooLate(string Basis) : NativeResponse;

    /// <summary>Current evidence supports no response timing; neither adequate service nor failure is assumed.</summary>
    public sealed record Unassessed(string Reason) : NativeResponse;
}

/// <summary>The dispatched pawns taken from observed native activity, counted and never priced here (the comparison prices labor).
/// A pawn without an occupancy observation is <see cref="Unread"/>: its current work is unassessed, not free.</summary>
public sealed record DisplacedWork(EquatableArray<string> NativeService, EquatableArray<string> NativeWork, EquatableArray<string> Unread)
{
    public int Count => NativeService.Count + NativeWork.Count;
}

/// <summary>The premise gate of an exact intervention, recomputed by every comparison from current facts and never stored on a
/// Method. <see cref="Stage"/> is the comparator stage at which the winner beat the native baseline.</summary>
public abstract record InterventionJustification(InterventionPremise Premise, NativeResponse Response, DisplacedWork Displaced,
    string Stage, EquatableArray<string> Uncertainty)
{
    public sealed record Justified(InterventionPremise Premise, NativeResponse Response, DisplacedWork Displaced,
        string Stage, EquatableArray<string> Uncertainty) : InterventionJustification(Premise, Response, Displaced, Stage, Uncertainty);

    public sealed record Refused(string Reason, InterventionPremise Premise, NativeResponse Response, DisplacedWork Displaced,
        string Stage, EquatableArray<string> Uncertainty) : InterventionJustification(Premise, Response, Displaced, Stage, Uncertainty);

    /// <summary>The winner is ordinary food/rest/recreation service alone: native service needs no exact order.</summary>
    public const string RoutineResponse = "native_service_response_without_intervention_premise";
    /// <summary>No premise, and the winner beat the baseline only below the safety floors.</summary>
    public const string MarginalGainWithoutPremise = "marginal_gain_without_premise";
    /// <summary>No premise, and the dispatched work serves a need whose native response the comparison left unprojected:
    /// its forecast benefit is the no-modeled-service counterfactual's.</summary>
    public const string NoModeledServiceRescue = "no_modeled_service_rescue";
    /// <summary>No premise, and the winner beat the baseline at a safety floor: a forecast is not dispatch authority.</summary>
    public const string ForecastBenefitWithoutPremise = "forecast_benefit_without_premise";
    /// <summary>A timely settings/work-creation response is supported and the premise is not an observed harm.</summary>
    public const string SettingsResponseSupported = "settings_response_supported";
    public const string PremiseUnassessedPrefix = "premise_unassessed:";
    /// <summary>The response state of a refusal decided before any premise needed a response.</summary>
    public const string NoPremiseToAnswer = "no_intervention_premise";
}

/// <summary>Pure intervention justification inside the shared comparison (RG-LABOR-004). It adds no threshold of its own: premises
/// are the domain's observations, responses its supported recovery leads, and "marginal" the comparator's own stages.</summary>
public static class InterventionRules
{
    /// <summary>The gate of a winning portfolio: null when it would install no exact order and is not ordinary service alone
    /// (native work and settings need no premise).</summary>
    internal static InterventionJustification? Gate(IReadOnlyCollection<string> selected, ForecastActivityPlan plan,
        ForecastConsequenceVector baseline, ForecastConsequenceVector winner, ForecastCoupledContext context, ItemFacts items)
    {
        var dispatch = Dispatched(selected, plan, context);
        if (dispatch.Length == 0 && !NativeNeedServices.RoutineOnly(plan, context)) return null;
        return Justify(plan, dispatch, baseline, winner, context, items);
    }

    /// <summary>The exact allocations a fresh start of <paramref name="selected"/> installs now: the ordinary-service-filtered
    /// dispatch (<see cref="CoordinatedActivityBinding.ForDispatch"/>) without the incumbents' work
    /// (<see cref="ForecastIncumbentHold.Dispatched"/>) and the unplaced planned sites, for the pawns
    /// <see cref="CoordinatedActivityBinding.OrderedAtStart"/> orders: the work the premise must justify.</summary>
    internal static ForecastActivityAllocation[] Dispatched(IReadOnlyCollection<string> selected, ForecastActivityPlan plan, ForecastCoupledContext context)
    {
        var unplaced = context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var (ids, allocations) = context.IncumbentHold.Dispatched(selected, CoordinatedActivityBinding.ForDispatch(plan, context).Allocations,
            context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal));
        var ordered = CoordinatedActivityBinding.OrderedAtStart(ids, allocations, context.Bindings, unplaced);
        return allocations.Where(a => !unplaced.Contains(a.Activity) && ordered.Contains(a.Pawn)).ToArray();
    }

    /// <summary>Whether the exact allocations <paramref name="dispatch"/> of the winning <paramref name="plan"/> are a meaningful
    /// intervention against the native baseline. Order: ordinary service alone is refused; then the premise of the work dispatched
    /// (practice: an assigned capability requirement; food work: an observed shortfall, else the food owner's credible risk); without
    /// one, the refusal names why the forecast won; with one, a supported timely native response refuses it unless the premise is
    /// an observed harm. An unassessed response does not refuse a premise: it remains named uncertainty. Food work is classified
    /// by the catalog's nutrition of the resources it moves (<paramref name="items"/>), not by current food targets.</summary>
    public static InterventionJustification Justify(ForecastActivityPlan plan, IReadOnlyList<ForecastActivityAllocation> dispatch,
        ForecastConsequenceVector baseline, ForecastConsequenceVector winner, ForecastCoupledContext context, ItemFacts items)
    {
        ForecastCoupledSelection.CompareConsequences(ForecastCoupledSelection.StagedConsequences.Complete(winner),
            ForecastCoupledSelection.StagedConsequences.Complete(baseline), out var stage);
        var displaced = DisplacedBy(dispatch, context);
        var uncertainty = new SortedSet<string>(displaced.Unread.Select(p => "displaced_work_unread:" + p), StringComparer.Ordinal);
        var unanswered = new NativeResponse.Unassessed(InterventionJustification.NoPremiseToAnswer);
        InterventionJustification Refuse(string reason, InterventionPremise premise, NativeResponse response) =>
            new InterventionJustification.Refused(reason, premise, response, displaced, stage, EquatableArray.From(uncertainty));
        if (NativeNeedServices.RoutineOnly(plan, context))
            return Refuse(InterventionJustification.RoutineResponse, new InterventionPremise.None(), unanswered);
        var food = ServesFood(dispatch, context, items, out var unclassified);
        var premise = PremiseOf(dispatch, food, unclassified, baseline, context, uncertainty);
        if (premise is InterventionPremise.Unassessed(var missing))
            return Refuse(InterventionJustification.PremiseUnassessedPrefix + missing, premise, unanswered);
        if (premise is InterventionPremise.None)
            return Refuse(food && FoodResponseUnprojected(context) ? InterventionJustification.NoModeledServiceRescue
                : stage is ForecastCoupledSelection.UrgentZeroStage or ForecastCoupledSelection.DeathFloorStage or ForecastCoupledSelection.BreakRiskStage
                    ? InterventionJustification.ForecastBenefitWithoutPremise : InterventionJustification.MarginalGainWithoutPremise,
                premise, unanswered);
        var response = premise is InterventionPremise.CapabilityOpportunity
            ? new NativeResponse.Unassessed("capability_native_response_unprojected") : FoodResponse(context.NativeFood);
        if (response is NativeResponse.Unassessed(var unprojected)) uncertainty.Add("native_response_unassessed:" + unprojected);
        if (response is NativeResponse.Supported && premise is not InterventionPremise.ObservedShortfall)
            return Refuse(InterventionJustification.SettingsResponseSupported, premise, response);
        return new InterventionJustification.Justified(premise, response, displaced, stage, EquatableArray.From(uncertainty));
    }

    private static InterventionPremise PremiseOf(IReadOnlyList<ForecastActivityAllocation> dispatch, bool food,
        InterventionPremise.Unassessed? unclassified, ForecastConsequenceVector baseline, ForecastCoupledContext context, SortedSet<string> uncertainty)
    {
        // Work moving a resource the catalog never read may be food work: no other premise found leaves it unassessed.
        var missing = unclassified;
        foreach (var allocation in dispatch)
        {
            if (!context.Bindings.TryGetValue((allocation.Activity, allocation.Pawn), out var binding) || binding.Practice is not { } practice) continue;
            foreach (var requirement in context.MaintenanceRequirements.Where(r => r.Requirement.Pawn.Value == allocation.Pawn
                && r.Requirement.Alternatives.Any(s => s.Skill == practice.Skill)))
            {
                // The baseline's projected gap applies the observed skill decay with no dispatched practice.
                switch (baseline.Maintenance.FirstOrDefault(m => m.Requirement == requirement.Requirement.Id)?.MissingXp)
                {
                    case ForecastMaintenanceGap.Projected(> 0):
                        uncertainty.Add("native_skill_xp_unprojected:" + allocation.Pawn + ":" + practice.Skill);
                        var met = requirement.Requirement.Alternatives.Any(s => requirement.Skills.FirstOrDefault(k => k.DefName == s.Skill)
                            is { Disabled: false } skill && skill.Level >= s.MinLevel);
                        return new InterventionPremise.CapabilityOpportunity(requirement.Requirement.Id, allocation.Pawn, practice.Skill,
                            met ? CapabilityGap.LapsesWithinHorizon : CapabilityGap.UnmetNow);
                    case ForecastMaintenanceGap.Projected:
                        continue;
                    case ForecastMaintenanceGap.Unprojectable:
                        missing ??= new("maintenance_unprojectable:" + requirement.Requirement.Id);
                        continue;
                    case null:
                        missing ??= new("maintenance_unassessed:" + requirement.Requirement.Id);
                        continue;
                }
            }
        }
        if (food)
        {
            foreach (var pawn in context.Needs.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
                if (pawn.Needs.FirstOrDefault(n => n.Kind == "food") is { } need && need.Level <= need.Threshold
                    && NativeNeedServices.Observe(context.Needs, pawn.Pawn, "food") == NativeNeedService.NoObservedService)
                    return new InterventionPremise.ObservedShortfall("food", pawn.Pawn, "at_or_below_threshold_without_observed_service");
            switch (context.NativeFood)
            {
                case NativeFoodAssessment.Reviewed { NeedsResponse: true } reviewed:
                    return new InterventionPremise.CredibleRisk(reviewed.Risk, reviewed.RunwayDays,
                        FoodReserveRules.RecoveryAtRunway(reviewed.RunwayDays, reviewed.Recovery));
                case NativeFoodAssessment.Unassessed(var reason):
                    missing ??= new("native_food:" + reason);
                    break;
            }
        }
        return missing ?? (InterventionPremise)new InterventionPremise.None();
    }

    /// <summary>The food owner's conditional recovery leads against usable runway: the timing of the settings/work-creation response.</summary>
    private static NativeResponse FoodResponse(NativeFoodAssessment food)
    {
        if (food is NativeFoodAssessment.Unassessed(var reason)) return new NativeResponse.Unassessed("native_food:" + reason);
        if (food is NativeFoodAssessment.NoConsumers) return new NativeResponse.Unassessed("native_food:no_consumers");
        var reviewed = (NativeFoodAssessment.Reviewed)food;
        return FoodReserveRules.RecoveryAtRunway(reviewed.RunwayDays, reviewed.Recovery) switch
        {
            FoodRecoveryTiming.BeforeRunway => new NativeResponse.Supported("food_recovery_before_runway"),
            FoodRecoveryTiming.AtOrAfterRunway => new NativeResponse.KnownTooLate("food_recovery_at_or_after_runway"),
            FoodRecoveryTiming.Unconfirmed => new NativeResponse.Unassessed("food_recovery_unconfirmed"),
            FoodRecoveryTiming.NoCapturedRecovery => new NativeResponse.Unassessed("food_recovery_not_captured"),
            var timing => throw new DomainException("food recovery timing:" + timing),
        };
    }

    /// <summary>Whether dispatched work uses food or produces or moves an edible resource: one whose catalog Nutrition is positive,
    /// the stock walk's rule (<see cref="ItemFacts.Nutrition"/>). The catalog records Nutrition for every item def that shows it, so
    /// a def the catalog read without one is no food; a resource the catalog never read (no market value or thing categories
    /// either, as on a frame without a catalog) is <paramref name="unclassified"/>: <c>nutrition_per_unit:&lt;resource&gt;</c>.</summary>
    private static bool ServesFood(IReadOnlyList<ForecastActivityAllocation> dispatch, ForecastCoupledContext context, ItemFacts items,
        out InterventionPremise.Unassessed? unclassified)
    {
        unclassified = null;
        var food = false;
        var activities = dispatch.Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        foreach (var activity in context.Activities.Activities.Where(a => activities.Contains(a.Id)))
        {
            food |= activity.RequiresFoodUse;
            foreach (var resource in activity.Outputs.Concat(activity.Inputs).Select(y => y.Resource).Order())
            {
                if (items.Nutrition.TryGetValue(resource, out var nutrition)) food |= nutrition > 0;
                else if (!items.Market.ContainsKey(resource) && !items.Categories.ContainsKey(resource))
                    unclassified ??= new("nutrition_per_unit:" + resource.Value);
            }
        }
        return food;
    }

    /// <summary>A pawn whose future food response the native comparison leaves unprojected (every food pawn but an ineligible one).</summary>
    private static bool FoodResponseUnprojected(ForecastCoupledContext context) => context.Needs.Pawns.Any(p => p.Needs.Any(n => n.Kind == "food")
        && NativeNeedServices.Observe(context.Needs, p.Pawn, "food") != NativeNeedService.Ineligible);

    private static DisplacedWork DisplacedBy(IReadOnlyList<ForecastActivityAllocation> dispatch, ForecastCoupledContext context)
    {
        List<string> service = [], work = [], unread = [];
        foreach (var pawn in dispatch.Select(a => a.Pawn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (context.Needs.Pawns.SingleOrDefault(p => p.Pawn == pawn)?.CurrentService is not null) service.Add(pawn);
            else if (context.Resources.Evidence.Pawns.SingleOrDefault(p => p.Pawn == pawn) is not { } observed) unread.Add(pawn);
            else if (observed.Occupied) work.Add(pawn);
        }
        return new(EquatableArray.From(service), EquatableArray.From(work), EquatableArray.From(unread));
    }
}
