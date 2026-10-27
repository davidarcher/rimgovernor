using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeNeedServicesTests
{
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
        Assert.True(selection.Rejected.Any(e => e.Reason == NativeNeedServices.RoutineResponse), string.Join(";", selection.Selection.Findings));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "ordinary", 100));

        // Exact services remain valid what-if evidence. Even a caller supplying that better forecast
        // directly cannot bypass the production binding guard and install a redundant service Method.
        var root = candidates.Alternatives[0];
        var scenario = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById([root])).Coupled!;
        var outcome = scenario.Alternatives.First(a => a.Feasible && a.Plan.Allocations.Length > 0);
        var forged = selection with { Selected = outcome, Selection = selection.Selection with
            { Selected = [root.Id], Plan = outcome.Plan } };
        Assert.True(NativeNeedServices.RoutineOnly(outcome.Plan, candidates.Context));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(forged, candidates, 1, "redundant", 100));
    }
}
