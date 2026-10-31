using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

/// <summary>What makes an exact pawn order meaningful now, read from current observations of the work the order serves.
/// A forecasted benefit is never a premise: the comparison's ranking says how much better, the premise says why act at all.</summary>
public abstract record InterventionPremise
{
    private InterventionPremise() { }

    /// <summary>A current observed deficit the dispatched work serves: the pawn's food at or below its native threshold while no
    /// native service is captured for it (<c>at_or_below_threshold_without_observed_service</c>), or its food, rest or joy at the
    /// zero floor beside a legal target while no native job serves it (<see cref="NativeNeedService.Unanswered"/>,
    /// <c>at_zero_without_native_service</c>).</summary>
    public sealed record ObservedShortfall(string Need, string Pawn, string Evidence) : InterventionPremise;

    /// <summary>The food owner's observed supply, reserve and recovery evidence (<see cref="NativeFoodAssessment.Reviewed"/>) shows a
    /// risk before anyone is harmed, and the dispatched work produces or moves food.</summary>
    public sealed record CredibleRisk(NativeFoodRisk Risk, double RunwayDays, FoodRecoveryTiming Timing) : InterventionPremise;

    /// <summary>An assigned capability requirement (<see cref="AssignedCapability"/>) the dispatched practice trains: unmet by the
    /// observed skill level now, or lapsing within the horizon under the observed skill decay.</summary>
    public sealed record CapabilityOpportunity(string Requirement, string Pawn, string Skill, CapabilityGap Gap) : InterventionPremise;

    /// <summary>The work dispatcher's observed calibration (<see cref="ProductionCalibration.Observed"/>) holds a closed short
    /// window of a product the dispatched work makes or feeds, for a cause exact orders can answer
    /// (<see cref="ProductionStall.AnsweredByOrders"/>). It is never inferred from omitted native production or an idle bench.</summary>
    public sealed record DemonstratedStall(ProductionStall Stall) : InterventionPremise;

    /// <summary>The review's observed construction calibration (<see cref="ConstructionCalibration.Observed"/>) holds a stall of the
    /// site the dispatched supply or finish serves, whose cause that work answers (<see cref="ConstructionStalls"/>). It is never
    /// inferred from elapsed ticks or an unplaced or forecast site.</summary>
    public sealed record StalledSite(ConstructionStall Stall) : InterventionPremise;

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

/// <summary>One dispatched pawn's exact orders and the premise they answer (task 4.2: each exact order needs its own premise).
/// <see cref="Refusal"/> is "" when it is justified; <see cref="EvidenceTick"/> is the tick of the observation the premise reads
/// (the needs read, or a stall's closed calibration window), carried onto the orders' <see cref="InterventionPurpose"/>.</summary>
public sealed record InterventionAssessment(string Pawn, EquatableArray<string> Activities, InterventionPremise Premise,
    NativeResponse Response, long EvidenceTick, string Refusal)
{
    public bool Justified => Refusal.Length == 0;

    /// <summary>The purpose a justified assessment gives its pawn's exact orders; null when it is refused.</summary>
    public InterventionPurpose? Purpose => Justified ? InterventionPurposes.Of(Premise, EvidenceTick) : null;
}

/// <summary>The premise gate of an exact intervention, recomputed by every comparison from current facts. Only its justified
/// purposes reach the Method (<see cref="InterventionPurpose"/>); the justification itself is never stored. <see cref="Stage"/>
/// is the comparator stage at which the winner beat the native baseline. <see cref="Interventions"/> holds one assessment per
/// dispatched pawn; the top-level premise and response are the first refused assessment's, or the first one's when all pass.</summary>
public abstract record InterventionJustification(InterventionPremise Premise, NativeResponse Response, DisplacedWork Displaced,
    string Stage, EquatableArray<string> Uncertainty)
{
    public EquatableArray<InterventionAssessment> Interventions { get; init; }
    /// <summary>The dispatched activities <see cref="InterventionRules.Readmitted"/> kept although shared binding withholds them
    /// (native or ordinary self-service work): set by <see cref="InterventionRules.Gate"/> on a justified verdict only, and passed
    /// to <see cref="CoordinatedActivityBinding.ForDispatch"/> by every caller that starts or counts this verdict's dispatch.</summary>
    public EquatableArray<string> Readmitted { get; init; }

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
    /// <summary>No other premise, and the observed calibration stall of the work's product has a cause exact orders cannot
    /// answer (no bench, full bill slots, missing ingredients, unstarted work or exhausted benches): <c>stall_not_answered_by_orders:&lt;product&gt;:&lt;cause&gt;</c>.</summary>
    public const string StallNotAnsweredByOrders = "stall_not_answered_by_orders";
    /// <summary>The evidence of an <see cref="InterventionPremise.ObservedShortfall"/> read from <see cref="NativeNeedService.Unanswered"/>.</summary>
    public const string UnansweredAtZero = "at_zero_without_native_service";
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
        var readmitted = Readmitted(plan, baseline, winner, context, items);
        var dispatch = Dispatched(selected, plan, context, readmitted);
        if (dispatch.Length == 0 && !NativeNeedServices.RoutineOnly(plan, context)) return null;
        var verdict = Justify(plan, dispatch, baseline, winner, context, items);
        return verdict is InterventionJustification.Justified ? verdict with { Readmitted = EquatableArray.From(dispatch.Select(a => a.Activity)
            .Where(readmitted.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) } : verdict;
    }

    /// <summary>
    /// The one pure place an activity shared binding withholds (<see cref="CoordinatedActivityBinding.Withheld"/>: native work or
    /// ordinary self-service) is re-admitted to dispatch. Each pawn's withheld activities that wait on nothing (no prerequisite,
    /// so no later job depends on an unobserved product) and have a binding are assessed together, before any dispatch filtering,
    /// exactly as <see cref="Justify"/> assesses dispatched work; an activity is re-admitted only when that assessment is justified
    /// with a purpose that answers it (<see cref="Answers"/>). The search's own dispatch evaluation re-admits nothing.
    /// </summary>
    internal static IReadOnlySet<string> Readmitted(ForecastActivityPlan plan, ForecastConsequenceVector baseline, ForecastConsequenceVector winner,
        ForecastCoupledContext context, ItemFacts items)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        var withheld = CoordinatedActivityBinding.Withheld(plan, context);
        var tasks = context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var unplaced = context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var candidates = plan.Allocations.Where(a => withheld.Contains(a.Activity) && !unplaced.Contains(a.Activity)
            && tasks.TryGetValue(a.Activity, out var task) && task.Prerequisites.Length == 0 && context.Bindings.ContainsKey((a.Activity, a.Pawn))).ToArray();
        if (candidates.Length == 0) return admitted;
        ForecastCoupledSelection.CompareConsequences(ForecastCoupledSelection.StagedConsequences.Complete(winner),
            ForecastCoupledSelection.StagedConsequences.Complete(baseline), out var stage);
        foreach (var group in candidates.GroupBy(a => a.Pawn, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var work = group.ToArray();
            if (Assess(group.Key, work, stage, baseline, context, items, new SortedSet<string>(StringComparer.Ordinal)).Purpose is not { } purpose) continue;
            foreach (var allocation in work.Where(a => Answers(purpose, a, context, items))) admitted.Add(allocation.Activity);
        }
        return admitted;
    }

    /// <summary>Whether a justified <paramref name="purpose"/> answers one withheld activity: the pawn's own eat, sleep or
    /// recreation answers only its own <see cref="InterventionPurposeKind.NeedShortfall"/> of food, rest or joy; a
    /// <see cref="InterventionPurposeKind.ProductionStall"/> is answered only by an ingredient delivery feeding its product
    /// (<see cref="Methods.ActivityTaskCandidate.Feeds"/>); a <see cref="InterventionPurposeKind.ConstructionStall"/> only by the supply or
    /// finish of its site whose cause the stall names; any other construction supply or finish answers nothing; other food work (<see cref="ServesFood"/>) answers a
    /// <see cref="InterventionPurposeKind.FoodRisk"/> or any food shortfall. Bill work itself (a <see cref="PawnActivityKind.Recipe"/>
    /// binding) is never re-admitted: native bills and work settings run it. Nothing else is re-admitted.</summary>
    private static bool Answers(InterventionPurpose purpose, ForecastActivityAllocation allocation, ForecastCoupledContext context, ItemFacts items)
    {
        var binding = context.Bindings[(allocation.Activity, allocation.Pawn)];
        if (binding.Kind == PawnActivityKind.Recipe) return false;
        if (NativeNeedServices.Routine(binding.Kind))
            return purpose.Kind == InterventionPurposeKind.NeedShortfall && purpose.Subject == NativeNeedServices.NeedOf(binding.Kind) && purpose.SubjectPawn == allocation.Pawn;
        if (purpose.Kind == InterventionPurposeKind.ProductionStall)
            return binding.Kind == PawnActivityKind.Deliver && binding.Feeds.Contains(purpose.Subject);
        if (purpose.Kind == InterventionPurposeKind.ConstructionStall)
            return SiteServed(binding) is { } served && served.Site == purpose.Subject
                && context.ConstructionCalibration is ConstructionCalibration.Observed observed
                && observed.Stalls.Any(s => s.Site == served.Site && s.Cause == served.Cause);
        if (SiteServed(binding) is not null) return false;
        return ServesFood([allocation], context, items, out _)
            && (purpose.Kind == InterventionPurposeKind.FoodRisk || purpose is { Kind: InterventionPurposeKind.NeedShortfall, Subject: "food" });
    }

    /// <summary>The exact allocations a fresh start of <paramref name="selected"/> installs now: the ordinary-service-filtered
    /// dispatch (<see cref="CoordinatedActivityBinding.ForDispatch"/>) without the incumbents' work
    /// (<see cref="ForecastIncumbentHold.Dispatched"/>) and the unplaced planned sites, for the pawns
    /// <see cref="CoordinatedActivityBinding.OrderedAtStart"/> orders: the work the premise must justify. <paramref name="readmitted"/>
    /// is <see cref="Readmitted"/>'s set.</summary>
    internal static ForecastActivityAllocation[] Dispatched(IReadOnlyCollection<string> selected, ForecastActivityPlan plan, ForecastCoupledContext context,
        IReadOnlySet<string>? readmitted = null)
    {
        var unplaced = context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var (ids, allocations) = context.IncumbentHold.Dispatched(selected, CoordinatedActivityBinding.ForDispatch(plan, context, readmitted).Allocations,
            context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal));
        var ordered = CoordinatedActivityBinding.OrderedAtStart(ids, allocations, context.Bindings, unplaced);
        return allocations.Where(a => !unplaced.Contains(a.Activity) && ordered.Contains(a.Pawn)).ToArray();
    }

    /// <summary>Whether the exact allocations <paramref name="dispatch"/> of the winning <paramref name="plan"/> are a meaningful
    /// intervention against the native baseline. Order: ordinary service alone, with nothing re-admitted, is refused; then, per dispatched pawn, the premise of
    /// that pawn's exact work (practice: an assigned capability requirement; food work: an observed shortfall, else the food owner's
    /// credible risk; bill work: the dispatcher's demonstrated stall), and the first pawn refused refuses the portfolio as
    /// <c>&lt;reason&gt;:&lt;pawn&gt;</c>; without
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
        if (dispatch.Count == 0 && NativeNeedServices.RoutineOnly(plan, context))
            return new InterventionJustification.Refused(InterventionJustification.RoutineResponse, new InterventionPremise.None(),
                new NativeResponse.Unassessed(InterventionJustification.NoPremiseToAnswer), displaced, stage, EquatableArray.From(uncertainty));
        // Each dispatched pawn's exact orders answer their own premise; a portfolio is justified only when every one is.
        var groups = dispatch.GroupBy(a => a.Pawn, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Pawn: g.Key, Work: (IReadOnlyList<ForecastActivityAllocation>)g.ToArray())).ToList();
        if (groups.Count == 0) groups.Add(("", []));
        var assessed = groups.Select(g => Assess(g.Pawn, g.Work, stage, baseline, context, items, uncertainty)).ToArray();
        var lead = assessed.FirstOrDefault(a => !a.Justified) ?? assessed[0];
        var all = EquatableArray.From(assessed);
        return lead.Justified
            ? new InterventionJustification.Justified(lead.Premise, lead.Response, displaced, stage, EquatableArray.From(uncertainty)) { Interventions = all }
            : new InterventionJustification.Refused(lead.Pawn.Length == 0 ? lead.Refusal : lead.Refusal + ":" + lead.Pawn, lead.Premise, lead.Response, displaced, stage,
                EquatableArray.From(uncertainty)) { Interventions = all };
    }

    /// <summary>One pawn's exact orders: their premise, the native response to it and, refused, why.</summary>
    private static InterventionAssessment Assess(string pawn, IReadOnlyList<ForecastActivityAllocation> dispatch, string stage,
        ForecastConsequenceVector baseline, ForecastCoupledContext context, ItemFacts items, SortedSet<string> uncertainty)
    {
        var activities = EquatableArray.From(dispatch.Select(a => a.Activity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var tick = context.Needs.Tick.Value;
        var unanswered = new NativeResponse.Unassessed(InterventionJustification.NoPremiseToAnswer);
        var food = ServesFood(dispatch, context, items, out var unclassified);
        var premise = PremiseOf(dispatch, food, unclassified, baseline, context, uncertainty, out var unansweredStall);
        if (premise is InterventionPremise.Unassessed(var missing))
            return new(pawn, activities, premise, unanswered, tick, InterventionJustification.PremiseUnassessedPrefix + missing);
        if (premise is InterventionPremise.None)
            return new(pawn, activities, premise, unanswered, tick, unansweredStall is { } stall
                    ? InterventionJustification.StallNotAnsweredByOrders + ":" + stall.Product.Value + ":" + Wire.Name(stall.Reason)
                : food && FoodResponseUnprojected(context) ? InterventionJustification.NoModeledServiceRescue
                : stage is ForecastCoupledSelection.UrgentZeroStage or ForecastCoupledSelection.DeathFloorStage or ForecastCoupledSelection.BreakRiskStage
                    ? InterventionJustification.ForecastBenefitWithoutPremise : InterventionJustification.MarginalGainWithoutPremise);
        var response = premise is InterventionPremise.CapabilityOpportunity ? new NativeResponse.Unassessed("capability_native_response_unprojected")
            : premise is InterventionPremise.DemonstratedStall ? new NativeResponse.Unassessed("stall_native_response_unprojected")
            : premise is InterventionPremise.StalledSite ? new NativeResponse.Unassessed("construction_native_response_unprojected")
            : premise is InterventionPremise.ObservedShortfall { Need: not "food" } shortfall
                ? new NativeResponse.Unassessed(shortfall.Need + "_native_response_unprojected")
            : FoodResponse(context.NativeFood);
        if (response is NativeResponse.Unassessed(var unprojected)) uncertainty.Add("native_response_unassessed:" + unprojected);
        var evidence = premise is InterventionPremise.DemonstratedStall(var demonstrated) ? demonstrated.ClosedTick
            : premise is InterventionPremise.StalledSite(var site) ? site.ClosedTick : tick;
        return new(pawn, activities, premise, response, evidence,
            response is NativeResponse.Supported && premise is not InterventionPremise.ObservedShortfall ? InterventionJustification.SettingsResponseSupported : "");
    }

    /// <summary>The premise of one pawn's dispatched work. Order: practice against an assigned capability requirement; food work
    /// against an observed shortfall, else the food owner's credible risk; the pawn's own sleep or recreation against its own
    /// unanswered rest or joy (<see cref="NativeNeedService.Unanswered"/>); then bill work against the dispatcher's observed
    /// calibration stall of a product the work makes or feeds (its activities and every activity waiting on them). A stall whose
    /// cause orders cannot answer is returned through <paramref name="unansweredStall"/> when no other premise stands.</summary>
    private static InterventionPremise PremiseOf(IReadOnlyList<ForecastActivityAllocation> dispatch, bool food,
        InterventionPremise.Unassessed? unclassified, ForecastConsequenceVector baseline, ForecastCoupledContext context, SortedSet<string> uncertainty,
        out ProductionStall? unansweredStall)
    {
        unansweredStall = null;
        // Work moving a resource the catalog never read may be food work: no other premise found leaves it unassessed.
        var missing = unclassified;
        foreach (var allocation in dispatch)
        {
            if (!context.Bindings.TryGetValue((allocation.Activity, allocation.Pawn), out var binding) || binding.Practice is not { } practice) continue;
            // A sparring partner's practice answers its partner's requirement: the premise is the practicing pawn's.
            foreach (var requirement in context.MaintenanceRequirements.Where(r => (r.Requirement.Pawn.Value == allocation.Pawn
                    || practice.Partner.Length != 0 && r.Requirement.Pawn.Value == practice.Partner)
                && r.Requirement.Alternatives.Any(s => s.Skill == practice.Skill)))
            {
                // The baseline's projected gap applies the observed skill decay with no dispatched practice.
                switch (baseline.Maintenance.FirstOrDefault(m => m.Requirement == requirement.Requirement.Id)?.MissingXp)
                {
                    case ForecastMaintenanceGap.Projected(> 0):
                        uncertainty.Add("native_skill_xp_unprojected:" + allocation.Pawn + ":" + practice.Skill);
                        var met = requirement.Requirement.Alternatives.Any(s => requirement.Skills.FirstOrDefault(k => k.DefName == s.Skill)
                            is { Disabled: false } skill && skill.Level >= s.MinLevel);
                        return new InterventionPremise.CapabilityOpportunity(requirement.Requirement.Id, requirement.Requirement.Pawn.Value, practice.Skill,
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
                if (pawn.Needs.FirstOrDefault(n => n.Kind == "food") is { } need && need.Level <= need.Threshold)
                    switch (NativeNeedServices.Observe(context.Needs, pawn.Pawn, "food"))
                    {
                        case NativeNeedService.NoObservedService:
                            return new InterventionPremise.ObservedShortfall("food", pawn.Pawn, "at_or_below_threshold_without_observed_service");
                        case NativeNeedService.Unanswered:
                            return new InterventionPremise.ObservedShortfall("food", pawn.Pawn, InterventionJustification.UnansweredAtZero);
                    }
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
        foreach (var allocation in dispatch)
            if (context.Bindings.TryGetValue((allocation.Activity, allocation.Pawn), out var service)
                && service.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation
                && NativeNeedServices.NeedOf(service.Kind) is var need
                && NativeNeedServices.Observe(context.Needs, allocation.Pawn, need) == NativeNeedService.Unanswered)
                return new InterventionPremise.ObservedShortfall(need, allocation.Pawn, InterventionJustification.UnansweredAtZero);
        if (StallOf(dispatch, context, ref missing, out unansweredStall) is { } stall) return new InterventionPremise.DemonstratedStall(stall);
        if (SiteStallOf(dispatch, context, ref missing) is { } site) return new InterventionPremise.StalledSite(site);
        return missing ?? (InterventionPremise)new InterventionPremise.None();
    }

    /// <summary>The placed site a construction binding serves and the stall cause it answers: a supply's container answers
    /// <see cref="ConstructionStallCause.SupplyUnstarted"/>, a finish's target <see cref="ConstructionStallCause.FinishUnstarted"/>.</summary>
    private static (string Site, ConstructionStallCause Cause)? SiteServed(Methods.ActivityTaskCandidate binding) =>
        binding.Kind == PawnActivityKind.Supply && binding.Container.Length != 0 ? (binding.Container, ConstructionStallCause.SupplyUnstarted)
        : binding is { Kind: PawnActivityKind.Work, Job: ConstructionFinishJob } ? (binding.Target, ConstructionStallCause.FinishUnstarted)
        : null;

    /// <summary>The native job of a construction finish binding (ConstructionMethodCandidates).</summary>
    private const string ConstructionFinishJob = "FinishFrame";

    /// <summary>The observed construction stall the dispatched supply or finish answers: a standing stall of the site it serves
    /// with the cause it answers (<see cref="SiteServed"/>). Construction work under unobserved progress is
    /// <see cref="ConstructionCalibration.UnobservedReason"/>.</summary>
    private static ConstructionStall? SiteStallOf(IReadOnlyList<ForecastActivityAllocation> dispatch, ForecastCoupledContext context,
        ref InterventionPremise.Unassessed? missing)
    {
        var served = dispatch.Select(a => context.Bindings.TryGetValue((a.Activity, a.Pawn), out var binding) ? SiteServed(binding) : null)
            .OfType<(string Site, ConstructionStallCause Cause)>().ToArray();
        if (served.Length == 0) return null;
        if (context.ConstructionCalibration is not ConstructionCalibration.Observed observed)
        {
            missing ??= new(((ConstructionCalibration.Unobserved)context.ConstructionCalibration).Reason);
            return null;
        }
        return observed.Stalls.FirstOrDefault(s => served.Contains((s.Site, s.Cause)));
    }

    /// <summary>The observed calibration stall the dispatched work answers: a standing closed short window of a product made
    /// or fed by the chain of the dispatched activities and every activity waiting on them (by prerequisites), or fed by an
    /// ingredient delivery in it (<see cref="ActivityTaskCandidate.Feeds"/>). Bill work under
    /// unobserved calibration is <see cref="ProductionCalibration.UnobservedReason"/>; a stall whose cause the dispatcher did
    /// not assess is <c>stall_cause_unassessed:&lt;product&gt;</c>; one whose cause orders cannot answer is returned through
    /// <paramref name="unanswered"/>.</summary>
    private static ProductionStall? StallOf(IReadOnlyList<ForecastActivityAllocation> dispatch, ForecastCoupledContext context,
        ref InterventionPremise.Unassessed? missing, out ProductionStall? unanswered)
    {
        unanswered = null;
        var chain = dispatch.Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        for (var grew = chain.Count > 0; grew;)
        {
            grew = false;
            foreach (var activity in context.Activities.Activities.Where(a => !chain.Contains(a.Id) && a.Prerequisites.Any(chain.Contains)))
                grew |= chain.Add(activity.Id);
        }
        var bill = context.Bindings.Where(b => chain.Contains(b.Key.Task) && (b.Value.Recipe is not null || b.Value.Feeds.Count != 0)).Select(b => b.Value).ToArray();
        if (bill.Length == 0) return null;
        var products = context.Activities.Activities.Where(a => chain.Contains(a.Id)).SelectMany(a => a.Outputs.Select(o => o.Resource.Value))
            .Concat(bill.Select(b => b.Recipe?.Resource ?? "").Where(r => r.Length != 0)).Concat(bill.SelectMany(b => b.Feeds))
            .ToHashSet(StringComparer.Ordinal);
        if (context.ProductionCalibration is not ProductionCalibration.Observed(var stalls))
        {
            missing ??= new(((ProductionCalibration.Unobserved)context.ProductionCalibration).Reason);
            return null;
        }
        foreach (var stall in stalls.Where(s => products.Contains(s.Product.Value)))
        {
            if (stall.AnsweredByOrders) return stall;
            if (stall.Reason == UnmetReason.RecipeWorkUnassessed) missing ??= new("stall_cause_unassessed:" + stall.Product.Value);
            else unanswered ??= stall;
        }
        return null;
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

    /// <summary>A pawn whose future food response the native comparison leaves unprojected (every food pawn but an ineligible or
    /// unanswered one, <see cref="NativeNeedServices.ConditionalKept"/>).</summary>
    private static bool FoodResponseUnprojected(ForecastCoupledContext context) => context.Needs.Pawns.Any(p => p.Needs.Any(n => n.Kind == "food")
        && !NativeNeedServices.ConditionalKept(NativeNeedServices.Observe(context.Needs, p.Pawn, "food")));

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
