using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeExecutionBindingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedResearchAndSowingRemainConditionalInvestmentsWithoutPawnOrders(bool research)
    {
        const double horizon = 3000;
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0, sourceCount: 0, horizon: horizon);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with { Location = new(1, 1) };
        var read = facts.ForecastObserved.Coupled! with
        {
            Research = research ? new() { Workers = [new("pawn", "bench", new(1, 1), "project", "Research", "Research", 1)
                { ServicePosition = new ServicePositionRead.Located(new(1, 1)) }], Findings = [] } : ActivityContinuationFixtures.NoResearch,
            Sowing = research ? CoupledFixtures.NoSowing : new() { Workers = [new("pawn", "zone", "GrowerSow", 0, 100, [new(1, 1)])
                { SkillWork = ProductiveLearningTests.Work() with { WorkAmount = 1, WorkQuantum = 1 } }], Findings = [] },
            Routes = [new("pawn", new(1, 1), new(1, 1), new RouteTicks.Reachable(0)) { FromStart = true }],
        };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read, Needs = NeedsFixtures.Read(100, pawn) } };
        var made = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)!.Priced();
        var task = Assert.Single(made.Context.Activities.Activities, t => t.ProjectProgress is not null);
        made = made with { Context = made.Context with { Investment = new Dictionary<string, double> { [task.ProjectProgress!.Project] = 1 } } };
        facts = facts with { CoupledAlternatives = made };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, made);
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Activity == task.Id);
        Assert.True(selected.SelectedVector.Value.Ledger.Investment < selected.BaselineVector.Value.Ledger.Investment);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selected, made, 1, "routine", 100));
        // Isolate the activity obligation from any separate schedule/priority changes.
        var settingsSettled = facts with { WorkCoverage = WorkCoverageState.Matched,
            ForecastObserved = facts.ForecastObserved with { WorkPawns = new([]) } };
        Assert.Equal(WorkAssignmentsReview.Settled, Rules.WorkAssignmentsOwed(settingsSettled, selected));
    }

    private static ForecastActivityAllocation Allocation(string task, string pawn, double start, double end) =>
        new(task, pawn, start, start, start, end, 0, end - start, end - start, 0);

    [Fact]
    public void MixedBindingStopsAtNativeWorkAndDropsDependentWorkWithoutHoldingOtherPawns()
    {
        var tasks = new ForecastActivity[] { new("before", "start", "start", []),
            new("native", "start", "start", []) { NativeExecution = true },
            new("after", "start", "start", []), new("dependent", "start", "start", ["native"]),
            new("transitive", "start", "start", ["dependent"]), new("helper-later", "start", "start", []),
            new("independent", "start", "start", []) };
        var rows = new[] { Allocation("before", "pawn", 0, 10), Allocation("native", "pawn", 10, 20),
            Allocation("after", "pawn", 20, 30), Allocation("dependent", "helper", 20, 30),
            Allocation("transitive", "another", 30, 40), Allocation("helper-later", "helper", 30, 40),
            Allocation("independent", "free", 0, 10) };
        var bindings = rows.ToDictionary(a => (a.Activity, a.Pawn), a => new ActivityTaskCandidate(a.Activity, a.Pawn,
            EquatableArray.From(tasks.Single(t => t.Id == a.Activity).Prerequisites), PawnActivityKind.Work, "site", "Build", "FinishFrame", 0, 1000, new(0, 0)));
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn()) with { Activities = new() { Activities = tasks, Deadline = 100 }, Bindings = bindings };
        var plan = new ForecastActivityPlan(rows, 40, 0);
        var batch = Assert.IsType<PawnActivities>(CoordinatedActivityBinding.StartSelected(new(["selected"], plan, [], 0, false),
            bindings, 1, "mixed", 100, context: context));
        Assert.Equal(["before", "independent"], batch.Tasks.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["free", "pawn"], batch.Orders.Select(o => o.Pawn).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NativeWorkCannotSmuggleARedundantEatingOrderThroughAMixedPortfolio()
    {
        var pawn = ForecastValueTests.Pawn() with { Targets = [NeedsFixtures.Food("meal", "Rice", 10, 0, 10, .5)] };
        var context = ForecastValueTests.Context(pawn) with
        {
            Activities = new() { Activities = [new("eat", "start", "start", []), new("native", "start", "start", []) { NativeExecution = true }], Deadline = 100 },
            Bindings = new Dictionary<(string, string), ActivityTaskCandidate> {
                [("eat", "pawn")] = new("eat", "pawn", [], PawnActivityKind.Eat, "meal", "", "", 1, 1000, new(0, 0)),
                [("native", "pawn")] = new("native", "pawn", [], PawnActivityKind.Work, "bench", "Research", "Research", 0, 1000, new(0, 0)) },
            NeedTasks = [new("eat", "pawn", "food", "meal", new("Rice"), 1, .5, [])],
        };
        var selection = new ForecastActivityOutcomeComparison(["mixed"], new([Allocation("eat", "pawn", 0, 10), Allocation("native", "pawn", 10, 20)], 20, 0), [], 0, false);
        Assert.Null(CoordinatedActivityBinding.StartSelected(selection, context.Bindings, 1, "mixed", 100, context: context));
    }
}
