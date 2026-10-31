using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;
using FoodRunway = RimGovernor.Brain.Policy.FoodRunway;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The shared comparison under <see cref="ForecastComparisonBasis.NativeOperation"/> treats an empty modeled schedule as
/// a no-modeled-service counterfactual: it never proves starvation, stalled production or a successful recovery, and an
/// unprojected need does not block an unrelated supported decision. Need-service-only roots creating no shortfall rows is
/// covered by <c>NativeWorkShortfallTests.AnOmittedServiceCounterfactualDoesNotCreateProductiveDemand</c>.</summary>
public sealed class NativeBaselineComparisonTests
{
    private const double Horizon = 300;
    private const string Pawn = "pawn";

    private static ColonyForecastRequest RequestOf(ReviewFacts facts) => ForecastCoupled.CoupledForecastRequestOf(new(100),
        Horizon / GameTime.TicksPerDay, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);

    private static NativeFoodAssessment Assess(ReviewFacts f) => NativeFoodAssessment.Of(f.FoodDays, f.FoodPlan, f.FoodReserve);

    /// <summary>A hungry, eligible pawn beside accessible stored rice (or none), with no producer anywhere.</summary>
    private static (ReviewFacts Facts, ForecastCoupledCandidates Candidates) FoodColony(bool target, NativeFoodAssessment? food = null)
    {
        var facts = ForecastCoupledSelectionTests.Review(sourceCount: 0);
        var original = facts.ForecastObserved.Needs!.Pawns[0];
        var rice = NeedsFixtures.Food("service", "Rice", 20, 0, 10, .5) with { Location = new(1, 1) };
        var pawn = original with { Location = new(1, 1), Needs = [NeedsFixtures.Rate("food", .2, .003, .3)], Targets = target ? [rice] : [] };
        if (target) facts = facts with { Census = facts.Census with { Resources = [new Quantity { DefName = "Rice", Units = 20 }] } };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = NeedsFixtures.Read(100, pawn),
            Coupled = facts.ForecastObserved.Coupled! with { Routes = [new(Pawn, pawn.Location, pawn.Location,
                new RouteTicks.Reachable(0)) { FromStart = true }] } } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced();
        if (food is not null) candidates = candidates with { Context = candidates.Context with { NativeFood = food } };
        return (facts with { CoupledAlternatives = candidates }, candidates);
    }

    private static ForecastNeedMetric BaselineFood(ForecastCoupledSelection selection, ForecastCoupledCandidates candidates) =>
        ForecastCoupledSelection.NativeExecutionOutcome(selection.Baseline, candidates.Context,
            ForecastCoupledSelection.NativeNeedBounds(candidates.Context.Needs, Horizon)).Needs[Pawn].Needs.Single(n => n.Kind == "food");

    [Fact]
    public void AvailableFoodWithoutASelectedMealLeavesBaselineStarvationUnassessedAndPrunesNothing()
    {
        var (facts, candidates) = FoodColony(target: true);
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(candidates.Context.Needs, Pawn, "food"));
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts), candidates);
        Assert.Equal(ForecastComparisonBasis.NativeOperation, selection.Basis);
        // The conditional no-service decay does reach zero; the native baseline must not present that as a starvation time.
        Assert.IsType<ForecastTerm.Assessed>(Assert.Single(selection.Baseline.Needs[Pawn].Needs).ZeroTicks);
        var food = BaselineFood(selection, candidates);
        Assert.IsType<ForecastTerm.Unassessed>(food.ZeroTicks);
        Assert.IsType<ForecastTerm.Unassessed>(food.FinalLevel);
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + Pawn + ":food", selection.Selection.Findings);
        Assert.False(selection.BaselineVector.Value.UrgentZero.Assessed);
        Assert.IsType<ForecastTerm.Unassessed>(selection.BaselineVector.Urgent[0]);
        // No unserved-pawn bound can be formed from an unassessed zero time, so nothing is pruned on this pawn.
        Assert.DoesNotContain(selection.Rejected, r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null);
        Assert.Equal(0, selection.SearchPhases.Sum(p => p.Pruned));
        Assert.Empty(selection.Selection.Selected);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "available-food", 100));
    }

    [Fact]
    public void AnUnobservedPawnsFutureNeedResponseIsUnassessedNotDecayed()
    {
        var pawn = ForecastValueTests.Pawn() with { Needs = [NeedsFixtures.Rate("food", .2, .003, .3)], Targets = [] };
        var context = ForecastValueTests.Context(pawn);
        var conditional = new ForecastCoupledOutcome(new([], 0, 0), true,
            new Dictionary<string, ForecastNeedsResult> { [Pawn] = ForecastValueTests.Timeline(pawn) }, []);
        Assert.True(Assert.Single(conditional.Needs[Pawn].Needs).ZeroTicks is ForecastTerm.Assessed);
        // The same colony's outcome, compared against a needs read that never saw the pawn.
        var blind = context with { Needs = NeedsFixtures.Read(100) };
        Assert.Equal(NativeNeedService.Unobserved, NativeNeedServices.Observe(blind.Needs, Pawn, "food"));
        var native = ForecastCoupledSelection.NativeExecutionOutcome(conditional, blind, ForecastCoupledSelection.NativeNeedBounds(blind.Needs, Horizon));
        var food = Assert.Single(native.Needs[Pawn].Needs);
        Assert.IsType<ForecastTerm.Unassessed>(food.FinalLevel);
        Assert.IsType<ForecastTerm.Unassessed>(food.ZeroTicks);
        Assert.IsType<ForecastTerm.Unassessed>(food.ThresholdExposureTicks);
        Assert.IsType<ForecastTerm.Unassessed>(food.ThresholdDepthTicks);
        Assert.IsType<ForecastMoodResult.Unprojected>(native.Needs[Pawn].Mood);
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + Pawn + ":food", native.Needs[Pawn].Findings);
    }

    [Fact]
    public void OmittedNativeProductionWithObservedStockClaimsNeitherStallNorStarvation()
    {
        var facts = NativeFoodAssessmentTests.Facts(10, 9);
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with { FoodConversions = [] } } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced();
        facts = facts with { CoupledAlternatives = candidates };
        var observed = Assert.IsType<NativeFoodAssessment.Reviewed>(Assess(facts));
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, observed.Risk);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts), candidates);
        // Both sides carry the observed assessment; the omitted cooking/harvest never clears or worsens it.
        Assert.Equal(candidates.Context.NativeFood, selection.BaselineVector.NativeFood);
        Assert.Equal(candidates.Context.NativeFood, selection.SelectedVector.NativeFood);
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, Assert.IsType<NativeFoodAssessment.Reviewed>(selection.BaselineVector.NativeFood).Risk);
        // The only unprojected evidence is the named need response, never a starvation or stall claim.
        Assert.All(selection.Selection.Findings, f => Assert.DoesNotMatch("(?i)starv|stall|shortfall", f));
        Assert.DoesNotContain(selection.Rejected, r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null);
        Assert.Empty(selection.Selection.Selected);
        Assert.Empty(ForecastCoupledSelection.SelectedProductionBills(selection, candidates));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "no-forced-production", 100));
    }

    [Fact]
    public void UnprojectedNeedResponseDoesNotBlockAnUnrelatedSupportedDecision()
    {
        // The assigned-practice fixture of CapabilityMaintenanceTests, but the practicing pawn is hungry with no
        // captured service: its food response is unassessed, yet the independent maintenance benefit still wins.
        var facts = ForecastPortfolioChoiceTests.Observed([], []);
        var requirement = new RoleDef(1, [new([new("Shooting", 1)])]);
        var ideology = CapabilityMaintenanceTests.Ideology(Pawn) with { Defs = new() { Roles = new Dictionary<string, RoleDef> { ["soldier"] = requirement } } };
        var roster = ((WorkPawnsRead)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Observed = p.Observed with { Biography = p.Observed.Biography with
            { Skills = [new() { Passion = Passion.None, DefName = "Shooting", Level = 1, Learning = CapabilityMaintenanceTests.Learning() }] } } }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Shooting", "target/" + i, new(i + 1, 1),
            "RimGovernor_TrainRange", "RimGovernor_TrainShooting", 0d, 100d, CapabilityMaintenanceTests.Learning())
        { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = HostileCensus.Of(0L), CriticalPatients = 0L, Ideology = new IdeologyMode.Installed(ideology), ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = new WorkPawnsRead(EquatableArray.From(roster)),
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } },
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!;
        var hungry = Assert.Single(generated.Context.Needs.Pawns);
        Assert.Equal(NativeNeedService.NoObservedService, NativeNeedServices.Observe(generated.Context.Needs, Pawn, "food"));
        Assert.True(hungry.Needs.Single(n => n.Kind == "food").Level < .3);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts with { CoupledAlternatives = generated }), generated);
        Assert.NotEmpty(selection.Selection.Selected);
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + Pawn + ":food", selection.Selection.Findings);
        // The unassessed term stays unassessed on both sides: neither zero nor success.
        Assert.IsType<ForecastTerm.Unassessed>(selection.BaselineVector.Urgent[0]);
        Assert.IsType<ForecastTerm.Unassessed>(selection.SelectedVector.Urgent[0]);
        Assert.False(selection.BaselineVector.Value.UrgentZero.Assessed);
        Assert.False(selection.SelectedVector.Value.UrgentZero.Assessed);
        var food = BaselineFood(selection, generated);
        Assert.IsType<ForecastTerm.Unassessed>(food.ZeroTicks);
        Assert.IsType<ForecastTerm.Unassessed>(food.FinalLevel);
        Assert.IsType<ForecastMaintenanceGap.Projected>(selection.Selected.Maintenance[0].MissingXp);
    }

    [Fact]
    public void ChangedFoodAvailabilityMovesTheNativeFoodAssessmentNotAnEatingTrajectory()
    {
        var (availableFacts, available) = FoodColony(target: true, Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        var (goneFacts, gone) = FoodColony(target: false, Assess(NativeFoodAssessmentTests.Facts(10, 10)));
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(available.Context.Needs, Pawn, "food"));
        Assert.Equal(NativeNeedService.NoObservedService, NativeNeedServices.Observe(gone.Context.Needs, Pawn, "food"));
        var before = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(availableFacts), available);
        var after = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(goneFacts), gone);
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, Assert.IsType<NativeFoodAssessment.Reviewed>(before.BaselineVector.NativeFood).Risk);
        Assert.Equal(NativeFoodRisk.RecoveryGap, Assert.IsType<NativeFoodAssessment.Reviewed>(after.BaselineVector.NativeFood).Risk);
        Assert.NotEqual(before.BaselineVector.NativeFood, after.BaselineVector.NativeFood);
        // In both states the eating metric is unprojected; preventive evidence flows through NativeFood alone.
        foreach (var (selection, candidates) in new[] { (before, available), (after, gone) })
        {
            Assert.IsType<ForecastTerm.Unassessed>(BaselineFood(selection, candidates).ZeroTicks);
            Assert.False(selection.BaselineVector.Value.UrgentZero.Assessed);
            Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "availability", 100));
        }
    }
}
