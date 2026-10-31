using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>RG-LABOR-004: an exact pawn order needs a concrete premise read from current observations of the work it serves,
/// separate from its forecasted benefit, and a native response that is not already supported in time. The premise is
/// recomputed by every comparison; nothing is stored on the Method.</summary>
public sealed class InterventionJustificationTests
{
    private const double Horizon = 300;
    private const string Pawn = "pawn";
    private const string Delivery = "deliver/rice";

    /// <summary>The catalog numbers the food rules read: rice shows Nutrition; wood is a read def without it.</summary>
    private static readonly ItemFacts Catalog = new()
    {
        Nutrition = new Dictionary<Resource, double> { [new("Rice")] = .05 },
        Market = new Dictionary<Resource, double> { [new("Rice")] = 1.1, [new("WoodLog")] = 1.2 },
    };

    private static ColonyForecastRequest RequestOf(ReviewFacts facts) => ForecastCoupled.CoupledForecastRequestOf(new(100),
        Horizon / GameTime.TicksPerDay, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);

    private static NativeFoodAssessment Assess(ReviewFacts f) => NativeFoodAssessment.Of(f.FoodDays, f.FoodPlan, f.FoodReserve);

    /// <summary>An eligible pawn at <paramref name="level"/> food (threshold .3) beside <paramref name="stock"/> units of stored rice (no food target at all when none), with the food
    /// owner's assessment, compared under native operation.</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates) FoodColony(double level, int stock, NativeFoodAssessment food)
    {
        var facts = ForecastCoupledSelectionTests.Review(sourceCount: 0);
        var original = facts.ForecastObserved.Needs!.Pawns[0];
        var rice = NeedsFixtures.Food("service", "Rice", stock, 0, 10, .5) with { Location = new(1, 1) };
        var pawn = original with { Location = new(1, 1), Needs = [NeedsFixtures.Rate("food", level, .003, .3)], Targets = stock > 0 ? [rice] : [] };
        if (stock > 0) facts = facts with { Census = facts.Census with { Resources = [new Quantity { DefName = "Rice", Units = stock }] } };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = NeedsFixtures.Read(100, pawn),
            Coupled = facts.ForecastObserved.Coupled! with { Routes = [new(Pawn, pawn.Location, pawn.Location,
                new RouteTicks.Reachable(0)) { FromStart = true }] } } };
        facts = WorkRules.ReviewWorkResources(facts with { Items = Catalog }, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced();
        candidates = candidates with { Context = candidates.Context with { NativeFood = food } };
        return (ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts with { CoupledAlternatives = candidates }), candidates), candidates);
    }

    /// <summary>Forged exact work producing food: one rice delivery the pawn could be ordered to carry, with no native execution.</summary>
    private static (ForecastCoupledContext Context, ForecastActivityPlan Plan) WithFoodDelivery(ForecastCoupledContext context)
    {
        var activity = new ForecastActivity(Delivery, "start", "start", []) { Outputs = [new(new Resource("Rice"), 4)] };
        var binding = new ActivityTaskCandidate(Delivery, Pawn, [], PawnActivityKind.Deliver, "rice", "", "", 4, 1000, new(1, 1)) { Resource = "Rice" };
        var bindings = new Dictionary<(string Task, string Pawn), ActivityTaskCandidate>(context.Bindings) { [(Delivery, Pawn)] = binding };
        var allocation = new ForecastActivityAllocation(Delivery, Pawn, 0, 0, 0, 100, 0, 100, 0, 0);
        return (context with { Activities = context.Activities with { Activities = [.. context.Activities.Activities, activity] }, Bindings = bindings },
            new([allocation], 100, 100));
    }

    private static InterventionJustification JustifyDelivery(ForecastCoupledSelection selection, ForecastCoupledContext context, ItemFacts? items = null)
    {
        var (forged, plan) = WithFoodDelivery(context);
        return InterventionRules.Justify(plan, plan.Allocations, selection.BaselineVector, selection.BaselineVector, forged, items ?? Catalog);
    }

    /// <summary>CapabilityMaintenanceTests' assigned Shooting fixture: a role requires Shooting 1 of a level-1 pawn whose XP
    /// (<paramref name="xp"/>) decays towards the loss threshold, beside a training range.</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates) Practice(double xp)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], []);
        var requirement = new RoleDef(1, [new([new("Shooting", 1)])]);
        var ideology = CapabilityMaintenanceTests.Ideology(Pawn) with { Defs = new() { Roles = new Dictionary<string, RoleDef> { ["soldier"] = requirement } } };
        var learning = CapabilityMaintenanceTests.Learning() with { Xp = xp };
        var roster = ((WorkPawnsRead)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Observed = p.Observed with { Biography = p.Observed.Biography with
            { Skills = [new() { Passion = Passion.None, DefName = "Shooting", Level = 1, Learning = learning }] } } }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Shooting", "target/" + i, new(i + 1, 1),
            "RimGovernor_TrainRange", "RimGovernor_TrainShooting", 0d, 100d, learning)
        { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = HostileCensus.Of(0L), CriticalPatients = 0L, Ideology = new IdeologyMode.Installed(ideology), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = new WorkPawnsRead(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!;
        return (ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts with { CoupledAlternatives = generated }), generated), generated);
    }

    /// <summary>PracticeHarmTests' production bout: no role holds a requirement, and tier 2 prices the Melee XP for raid harm.</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates) PricedBout(NativeFoodAssessment food)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
        var learning = CapabilityMaintenanceTests.Learning() with { Xp = 0d };
        var roster = ((WorkPawnsRead)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Observed = p.Observed with { Biography = p.Observed.Biography with
            { Skills = [new() { DefName = "Melee", Passion = Passion.None, Level = 1, Learning = learning }] } } }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Melee", "target/" + i, new(i + 1, 1), "RimGovernor_Spar", "RimGovernor_Spar",
            0d, 100d, learning) { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = HostileCensus.Of(0L), CriticalPatients = 0L, ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = new WorkPawnsRead(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!;
        var investment = roster.ToDictionary(p => ProjectGraph.SkillKey(p.Id.Value, "Melee"), _ => 1d, StringComparer.Ordinal);
        generated = generated with { Context = (generated.Context with { Investment = investment, NativeFood = food }).Priced() };
        var request = RequestOf(facts with { CoupledAlternatives = generated }) with { Coupled = generated.Context };
        return (ForecastCoupledSelection.CompareActivityOutcomes(request, generated), generated);
    }

    [Fact]
    public void AMarginalGainUnderAdequateConditionsIsRefused()
    {
        var adequate = Assess(NativeFoodAssessmentTests.Facts(10, 9));
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, Assert.IsType<NativeFoodAssessment.Reviewed>(adequate).Risk);
        var (selection, candidates) = PricedBout(adequate);
        // The bout leads the shared comparison on the ledger alone and trains no assigned capability.
        Assert.Empty(candidates.Context.MaintenanceRequirements);
        var refused = Assert.IsType<InterventionJustification.Refused>(selection.Justification);
        Assert.Equal(InterventionJustification.MarginalGainWithoutPremise, refused.Reason);
        Assert.Equal(ForecastCoupledSelection.LedgerStage, refused.Stage);
        Assert.IsType<InterventionPremise.None>(refused.Premise);
        Assert.Contains(selection.Rejected, r => r.Reason == InterventionJustification.MarginalGainWithoutPremise && r.Commitments.Count > 0);
        // The native baseline stands: nothing is selected or started.
        Assert.Empty(selection.Selection.Selected);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "marginal", 100));
    }

    [Fact]
    public void ARescueFromANoModeledServiceBaselineIsRefused()
    {
        // NativeNeedServicesTests' shape: a hungry pawn beside accessible rice. The forecast's benefit of carrying food rests on the
        // omitted native eating: the comparison left that response unprojected, and no observation makes the order meaningful.
        var (selection, candidates) = FoodColony(.2, stock: 20, Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(candidates.Context.Needs, Pawn, "food"));
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + Pawn + ":food", selection.Selection.Findings);
        var refused = Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(selection, candidates.Context));
        Assert.Equal(InterventionJustification.NoModeledServiceRescue, refused.Reason);
        Assert.IsType<InterventionPremise.None>(refused.Premise);
    }

    [Fact]
    public void PreventionFromSupportedRecoveryEvidenceIsJustifiedBeforeHarm()
    {
        // Ten days of usable stock, but every captured recovery arrives at day twelve. The pawn is fed and has food access.
        var risk = Assess(NativeFoodAssessmentTests.Facts(10, 12));
        var (selection, candidates) = FoodColony(.9, stock: 20, risk);
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(candidates.Context.Needs, Pawn, "food"));
        var justified = Assert.IsType<InterventionJustification.Justified>(JustifyDelivery(selection, candidates.Context));
        var premise = Assert.IsType<InterventionPremise.CredibleRisk>(justified.Premise);
        Assert.Equal((NativeFoodRisk.RecoveryGap, FoodRecoveryTiming.AtOrAfterRunway, 10d), (premise.Risk, premise.Timing, premise.RunwayDays));
        Assert.Equal(new NativeResponse.KnownTooLate("food_recovery_at_or_after_runway"), justified.Response);
    }

    [Fact]
    public void SettingsKnownTooLateJustifyImmediateActionWithoutAnExactFailurePrediction()
    {
        var (selection, candidates) = FoodColony(.2, stock: 20, Assess(NativeFoodAssessmentTests.Facts(10, 12)));
        // Neither side of the comparison predicts vanilla starvation: the food terms stay unassessed on both vectors.
        foreach (var vector in new[] { selection.BaselineVector, selection.SelectedVector })
        {
            Assert.IsType<ForecastTerm.Unassessed>(vector.Urgent[0]);
            Assert.False(vector.Value.UrgentZero.Assessed);
        }
        var justified = Assert.IsType<InterventionJustification.Justified>(JustifyDelivery(selection, candidates.Context));
        Assert.IsType<InterventionPremise.CredibleRisk>(justified.Premise);
        Assert.IsType<NativeResponse.KnownTooLate>(justified.Response);
    }

    [Fact]
    public void ASupportedTimelySettingsResponseRefusesTheIntervention()
    {
        // Two days of stock under the seasonal minimum, with a captured recovery at day one: native settings answer in time.
        var short_ = Assess(NativeFoodAssessmentTests.Facts(2, 1));
        Assert.Equal(NativeFoodRisk.SupplyBelowMinimum, Assert.IsType<NativeFoodAssessment.Reviewed>(short_).Risk);
        var (selection, candidates) = FoodColony(.9, stock: 20, short_);
        var refused = Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(selection, candidates.Context));
        Assert.Equal(InterventionJustification.SettingsResponseSupported, refused.Reason);
        Assert.IsType<InterventionPremise.CredibleRisk>(refused.Premise);
        Assert.Equal(new NativeResponse.Supported("food_recovery_before_runway"), refused.Response);
        // An observed harm is not answered by a later recovery: a hungry pawn without any food target has no observed service, and the
        // delivery is still food work by the catalog nutrition of the rice it moves.
        var (hungry, starving) = FoodColony(.2, stock: 0, short_);
        var shortfall = Assert.IsType<InterventionJustification.Justified>(JustifyDelivery(hungry, starving.Context));
        Assert.Equal(new InterventionPremise.ObservedShortfall("food", Pawn, "at_or_below_threshold_without_observed_service"), shortfall.Premise);
        // Missing food evidence names the blocker instead of assuming a crisis or adequate service.
        var (unread, blind) = FoodColony(.9, stock: 20, new NativeFoodAssessment.Unassessed("food_runway_unobserved"));
        var unassessed = Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(unread, blind.Context));
        Assert.Equal(InterventionJustification.PremiseUnassessedPrefix + "native_food:food_runway_unobserved", unassessed.Reason);
    }

    [Fact]
    public void FreshNativeProgressInvalidatesAStalePremise()
    {
        // Recovery evidence: the risk cleared by fresh native stock leaves the same delivery without its premise.
        var (risky, before) = FoodColony(.9, stock: 20, Assess(NativeFoodAssessmentTests.Facts(10, 12)));
        Assert.IsType<InterventionJustification.Justified>(JustifyDelivery(risky, before.Context));
        var (recovered, after) = FoodColony(.9, stock: 20, Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(recovered, after.Context));

        // Capability: native XP lifted the skill clear of decay. The next review selects nothing and starts nothing, and the
        // previous review's exact plan cannot be carried onto the fresh review: it holds no justification.
        var (lapsing, first) = Practice(-95d);
        Assert.IsType<InterventionJustification.Justified>(lapsing.Justification);
        Assert.NotNull(ForecastCoupledSelection.StartCoupledActivity(lapsing, first, 1, "first", 100));
        var (progressed, second) = Practice(100d);
        Assert.Null(progressed.Justification);
        Assert.Empty(progressed.Selection.Selected);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(progressed, second, 1, "second", 100));
        var stale = progressed with { Selected = lapsing.Selected, Selection = progressed.Selection with
            { Selected = lapsing.Selection.Selected, Plan = lapsing.Selection.Plan } };
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.StartCoupledActivity(stale, second, 1, "stale", 100));
    }

    [Fact]
    public void AnOrdinaryServiceWinnerKeepsTheRoutineRefusal()
    {
        var (selection, candidates) = FoodColony(.2, stock: 20, Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        var root = candidates.Alternatives[0];
        var request = RequestOf(ForecastCoupledSelectionTests.Review(sourceCount: 0)) with { Coupled = candidates.Context };
        var outcome = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById([root])).Coupled!
            .Alternatives.First(a => a.Feasible && a.Plan.Allocations.Length > 0);
        Assert.True(NativeNeedServices.RoutineOnly(outcome.Plan, candidates.Context));
        var refused = Assert.IsType<InterventionJustification.Refused>(
            InterventionRules.Justify(outcome.Plan, [], selection.BaselineVector, selection.BaselineVector, candidates.Context, Catalog));
        Assert.Equal(InterventionJustification.RoutineResponse, refused.Reason);
        Assert.Equal("native_service_response_without_intervention_premise", refused.Reason);
    }

    [Fact]
    public void AssignedPracticeDispatchesOnlyWhileItsObservedCapabilityPremiseHolds()
    {
        var (selection, candidates) = Practice(-95d);
        var justified = Assert.IsType<InterventionJustification.Justified>(selection.Justification);
        var premise = Assert.IsType<InterventionPremise.CapabilityOpportunity>(justified.Premise);
        Assert.Equal((Pawn, "Shooting", CapabilityGap.LapsesWithinHorizon), (premise.Pawn, premise.Skill, premise.Gap));
        // The training work type's own response timing is not projected: named, neither too late nor adequate.
        Assert.IsType<NativeResponse.Unassessed>(justified.Response);
        Assert.Contains("native_skill_xp_unprojected:" + Pawn + ":Shooting", justified.Uncertainty);
        Assert.NotNull(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "practice", 100));

        // The same winning practice without the assigned requirement it trains has no premise: its maintenance_xp win is marginal.
        var unassigned = candidates.Context with { MaintenanceRequirements = [] };
        var dispatch = InterventionRules.Dispatched(selection.Selection.Selected, selection.Selected.Plan, unassigned);
        Assert.NotEmpty(dispatch);
        var refused = Assert.IsType<InterventionJustification.Refused>(InterventionRules.Justify(selection.Selected.Plan, dispatch,
            selection.BaselineVector, selection.SelectedVector, unassigned, new ItemFacts()));
        Assert.Equal(InterventionJustification.MarginalGainWithoutPremise, refused.Reason);
        Assert.Equal("maintenance_xp", refused.Stage);
    }

    [Fact]
    public void FoodWorkIsClassifiedByCatalogNutritionNotByCurrentTargets()
    {
        // A hungry pawn with no food target at all: the rice delivery is food work because the catalog shows rice's Nutrition.
        var (selection, candidates) = FoodColony(.2, stock: 0, Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        Assert.Empty(candidates.Context.Needs.Pawns.Single(p => p.Pawn == Pawn).Targets);
        Assert.IsType<InterventionPremise.ObservedShortfall>(
            Assert.IsType<InterventionJustification.Justified>(JustifyDelivery(selection, candidates.Context)).Premise);
        // A def the catalog read without Nutrition is no food: the same delivery has no food premise.
        var inedible = new ItemFacts { Market = new Dictionary<Resource, double> { [new("Rice")] = 1.1 } };
        var marginal = Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(selection, candidates.Context, inedible));
        Assert.Equal(InterventionJustification.MarginalGainWithoutPremise, marginal.Reason);
        // A resource the catalog never read is not assumed inedible: the classification is named unassessed.
        var unread = Assert.IsType<InterventionJustification.Refused>(JustifyDelivery(selection, candidates.Context, new ItemFacts()));
        Assert.Equal(InterventionJustification.PremiseUnassessedPrefix + "nutrition_per_unit:Rice", unread.Reason);
        Assert.Equal(new InterventionPremise.Unassessed("nutrition_per_unit:Rice"), unread.Premise);
    }
}
