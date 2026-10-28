using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeNeedServicesTests
{
    [Theory]
    [InlineData("rest")]
    [InlineData("joy")]
    public void UncapturedNativeServiceDoesNotProveFutureDeprivation(string kind)
    {
        const double horizon = 300;
        var facts = ForecastCoupledSelectionTests.Review(sourceCount: 0);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        { Needs = [NeedsFixtures.Rate(kind, .2, .003, .3)], Targets = [] };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = NeedsFixtures.Read(100, pawn) } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!.Priced();
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Equal(NativeNeedService.NoObservedService, NativeNeedServices.Observe(candidates.Context.Needs, pawn.Pawn, kind));
        Assert.True(Assert.Single(selection.Baseline.Needs[pawn.Pawn].Needs).ThresholdExposureTicks.Number() > 0);
        var native = ForecastCoupledSelection.NativeExecutionOutcome(selection.Baseline, candidates.Context,
            ForecastCoupledSelection.NativeNeedBounds(candidates.Context.Needs, horizon));
        Assert.IsType<ForecastTerm.Unassessed>(Assert.Single(native.Needs[pawn.Pawn].Needs).ThresholdExposureTicks);
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + pawn.Pawn + ":" + kind, selection.Selection.Findings);
        Assert.Empty(selection.Selection.Selected);
        Assert.IsType<ForecastTerm.Unassessed>(selection.BaselineVector.BreakRisk);
    }

    [Fact]
    public void CurrentNativeAccessDoesNotRequireACompleteFutureForecast()
    {
        var pawn = NeedsFixtures.Pawn("pawn") with { Targets = [NeedsFixtures.Food("meal", "Rice", 2, 0, 10, .5)
            with { Unsupported = ["ingestion_outcomes_and_poisoning"] }] };
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(NeedsFixtures.Read(100, pawn), "pawn", "food"));
        Assert.Equal(NativeNeedService.NoObservedService, NativeNeedServices.Observe(NeedsFixtures.Read(100, pawn with { Targets = [] }), "pawn", "food"));
        Assert.Equal(NativeNeedService.Unobserved, NativeNeedServices.Observe(NeedsFixtures.Read(100), "pawn", "food"));
        Assert.Equal(NativeNeedService.Ineligible, NativeNeedServices.Observe(NeedsFixtures.Read(100, pawn with { State = "downed" }), "pawn", "food"));
        Assert.Equal(NativeNeedService.InProgress, NativeNeedServices.Observe(NeedsFixtures.Read(100, pawn with
            { Targets = [], CurrentService = new("", "", 0, "meal", "food", new(0, 0), 10, .1) { Unsupported = ["remaining_duration"] } }), "pawn", "food"));
    }

    [Theory]
    [InlineData("food")]
    [InlineData("rest")]
    [InlineData("joy")]
    public void ANoServiceCounterfactualCannotAuthorizeARedundantStandaloneService(string kind)
    {
        const double horizon = 300;
        var facts = ForecastCoupledSelectionTests.Review(sourceCount: 0);
        var original = facts.ForecastObserved.Needs!.Pawns[0];
        var target = (kind == "food" ? NeedsFixtures.Food("service", "Rice", 20, 0, 10, .5)
            : NeedsFixtures.Service("service", kind, 0, .01)) with { Location = new(1, 1) };
        var pawn = original with { Location = new(1, 1), Needs = [NeedsFixtures.Rate(kind, .2, .003, .3)], Targets = [target] };
        if (kind == "food") facts = facts with { Census = facts.Census with
            { Resources = [new Quantity { DefName = "Rice", Units = 20 }] } };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = NeedsFixtures.Read(100, pawn),
            Coupled = facts.ForecastObserved.Coupled! with { Routes = [new("pawn", pawn.Location, pawn.Location,
                new RouteTicks.Reachable(0)) { FromStart = true }] } } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!.Priced();
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Empty(selection.Selection.Selected);
        var bounds = ForecastCoupledSelection.NativeNeedBounds(candidates.Context.Needs, horizon);
        var metric = Assert.Single(ForecastCoupledSelection.NativeExecutionOutcome(selection.Baseline, candidates.Context, bounds).Needs["pawn"].Needs);
        Assert.IsType<ForecastTerm.Unassessed>(metric.ThresholdExposureTicks);
        Assert.Contains(ForecastCoupledSelection.NativeResponseUnprojectedPrefix + "pawn:" + kind, selection.Selection.Findings);
        if (kind is "food" or "rest") Assert.False(selection.BaselineVector.Value.UrgentZero.Assessed);
        Assert.IsType<ForecastTerm.Unassessed>(selection.BaselineVector.BreakRisk);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "ordinary", 100));

        // Exact services remain valid what-if evidence. Even a caller supplying that better forecast
        // directly cannot bypass the production binding guard and install a redundant service Method.
        var root = candidates.Alternatives[0];
        var scenario = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById([root])).Coupled!;
        var outcome = scenario.Alternatives.First(a => a.Feasible && a.Plan.Allocations.Length > 0);
        var nativeSelected = Assert.Single(ForecastCoupledSelection.NativeExecutionOutcome(outcome, candidates.Context, bounds).Needs["pawn"].Needs);
        Assert.IsType<ForecastTerm.Unassessed>(nativeSelected.FinalLevel);
        Assert.IsType<ForecastTerm.Unassessed>(nativeSelected.ThresholdExposureTicks);
        var forged = selection with { Selected = outcome, Selection = selection.Selection with
            { Selected = [root.Id], Plan = outcome.Plan } };
        Assert.True(NativeNeedServices.RoutineOnly(outcome.Plan, candidates.Context));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(forged, candidates, 1, "redundant", 100));

        // The original no-service trajectory is still explicit what-if evidence; its positive exposure
        // cannot become either a predicted native failure or an invented successful response.
        var counterfactual = Assert.Single(selection.Baseline.Needs["pawn"].Needs);
        Assert.True(counterfactual.ThresholdExposureTicks.Number() > 0);
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("joy", false)]
    [InlineData("joy", true)]
    public void HypotheticalRecoveryCannotImproveTheNativeAssessmentAndSafeBoundsDoNotRequireRecovery(string kind, bool safe)
    {
        const double horizon = 300;
        var target = kind == "food" ? NeedsFixtures.Food("service", "Rice", 20, 0, 10, .5)
            : NeedsFixtures.Service("service", kind, 0, .01);
        var pawn = ForecastValueTests.Pawn() with { Needs = [NeedsFixtures.Rate(kind, safe ? .9 : .2, safe ? .00001 : .003, .3)], Targets = [target] };
        var context = ForecastValueTests.Context(pawn) with { NeedTasks = [new("service", "pawn", kind, "service", new("Rice"), 1, .5, [])] };
        var perfect = new ForecastNeedMetric(kind, new ForecastTerm.Assessed(1), new ForecastTerm.Assessed(0))
            { ZeroTicks = new ForecastTerm.Assessed(0), ThresholdDepthTicks = new ForecastTerm.Assessed(0) };
        var conditional = new ForecastCoupledOutcome(new([new("service", "pawn", 0, 0, 0, 10, 0, 10, 0, 0)], 10, 0), true,
            new Dictionary<string, ForecastNeedsResult> { ["pawn"] = ForecastValueTests.Timeline(pawn) with { Needs = [perfect] } }, []);
        var bounds = ForecastCoupledSelection.NativeNeedBounds(context.Needs, horizon);
        var native = ForecastCoupledSelection.NativeExecutionOutcome(conditional, context, bounds);
        var metric = Assert.Single(native.Needs["pawn"].Needs);
        Assert.IsType<ForecastTerm.Unassessed>(metric.FinalLevel);
        if (safe)
        {
            Assert.Equal(0, metric.ZeroTicks.Number());
            Assert.Equal(0, metric.ThresholdExposureTicks.Number());
        }
        else
        {
            Assert.IsType<ForecastTerm.Unassessed>(metric.ZeroTicks);
            Assert.IsType<ForecastTerm.Unassessed>(metric.ThresholdExposureTicks);
            Assert.IsType<ForecastMoodResult.Unprojected>(native.Needs["pawn"].Mood);
        }
        var omitted = ForecastCoupledSelection.NativeExecutionOutcome(conditional with { Plan = new([], 0, 0) }, context, bounds);
        Assert.Equal(metric, Assert.Single(omitted.Needs["pawn"].Needs));
        Assert.Equal(1, Assert.Single(conditional.Needs["pawn"].Needs).FinalLevel.Number());
    }
}
