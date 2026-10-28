using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeExecutionBindingTests
{
    [Fact]
    public void SelectedHarvestAndRawDeliveryStayConditionalWithoutOwningTheWorkerOrFutureFood()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .1, fall: .00001, expiry: 100000);
        var made = facts.CoupledAlternatives!;
        var harvests = made.Context.Activities.Activities.Where(t => t.Inputs.Count == 0 && t.Outputs.Count > 0).ToArray();
        Assert.NotEmpty(harvests);
        Assert.All(harvests, t => Assert.True(t.NativeExecution));
        var deliveries = made.Context.Activities.Activities.Where(t => t.Inputs.Count > 0 && t.Outputs.Count > 0).ToArray();
        Assert.NotEmpty(deliveries);
        Assert.All(deliveries, t => Assert.True(t.NativeExecution));
        var native = Assert.IsType<ForecastCoupledSelection>(Rules.InspectReview(facts, new(), Rules.DefaultPlannerSettings()).CoupledSelection);
        Assert.Empty(native.Selection.Selected);
        Assert.False(native.BaselineVector.Value.UrgentZero.Assessed);
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 1, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, made, basis: ForecastComparisonBasis.ConditionalScenario);
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.Contains(selected.Selected.Plan.Allocations, a => harvests.Any(t => t.Id == a.Activity));
        Assert.True(selected.SelectedVector.Urgent[0].Number() < selected.BaselineVector.Urgent[0].Number());
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selected, made, 1, "routine-food", 100));
        Assert.Empty(facts.Census.Resources); // A conditional yield has not become observed stock.
        var settingsSettled = facts with { WorkCoverage = WorkCoverageState.Matched,
            ForecastObserved = facts.ForecastObserved with { WorkPawns = new([]) } };
        Assert.Equal(WorkAssignmentsReview.Settled, Rules.WorkAssignmentsOwed(settingsSettled, native));
    }

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

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("joy", false)]
    [InlineData("joy", true)]
    public void ProductivePortfolioCannotOwnNativeServiceOrItsUnobservedFutureWork(string kind, bool inProgress)
    {
        var target = kind == "food" ? NeedsFixtures.Food("service", "Rice", 10, 0, 10, .5)
            : NeedsFixtures.Service("service", kind, 0, .01);
        var pawn = ForecastValueTests.Pawn() with { Targets = inProgress ? [] : [target],
            CurrentService = inProgress ? new("", "", 0, "service", kind, new(0, 0), 10, .1) { Unsupported = [] } : null };
        var tasks = new ForecastActivity[] { new("before", "start", "start", []), new("service", "start", "start", []),
            new("after", "start", "start", []), new("dependent", "start", "start", ["service"]),
            new("helper-later", "start", "start", []), new("transitive", "start", "start", ["dependent"]),
            new("independent", "start", "start", []) };
        var rows = new[] { Allocation("before", "pawn", 0, 10), Allocation("service", "pawn", 10, 20),
            Allocation("after", "pawn", 20, 30), Allocation("dependent", "helper", 20, 30),
            Allocation("helper-later", "helper", 30, 40), Allocation("transitive", "another", 30, 40),
            Allocation("independent", "free", 0, 10) };
        var bindings = rows.ToDictionary(a => (a.Activity, a.Pawn), a => new ActivityTaskCandidate(a.Activity, a.Pawn,
            EquatableArray.From(tasks.Single(t => t.Id == a.Activity).Prerequisites), PawnActivityKind.Work, "site", "Build", "FinishFrame", 0, 1000, new(0, 0)));
        bindings[("service", "pawn")] = new("service", "pawn", [], kind == "food" ? PawnActivityKind.Eat
            : kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation, "service", "", "", 1, 1000, new(0, 0));
        var context = ForecastValueTests.Context(pawn) with { Activities = new() { Activities = tasks, Deadline = 100 },
            Bindings = bindings, NeedTasks = [new("service", "pawn", kind, "service", new(kind == "food" ? "Rice" : ""), 1, .5, [])] };
        var plan = new ForecastActivityPlan(rows, 40, 0);
        var batch = Assert.IsType<PawnActivities>(CoordinatedActivityBinding.StartSelected(new(["mixed"], plan, [], 0, false),
            bindings, 1, "mixed", 100, context: context));
        Assert.Equal(inProgress ? ["independent"] : ["before", "independent"], batch.Tasks.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(inProgress ? ["free"] : ["free", "pawn"], batch.Orders.Select(o => o.Pawn).Order(StringComparer.Ordinal));
        Assert.Equal(7, plan.Allocations.Length); // Detailed what-if evidence remains intact.
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("joy")]
    public void NoCapturedServiceObjectCannotAuthorizeRestOrRecreationTakeover(string kind)
    {
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn() with { Targets = [] }) with
        {
            Activities = new() { Activities = [new("service", "start", "start", [])], Deadline = 100 },
            Bindings = new Dictionary<(string, string), ActivityTaskCandidate> { [("service", "pawn")] = new("service", "pawn", [],
                kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation, "future-service", "", "", 0, 1000, new(0, 0)) },
            NeedTasks = [new("service", "pawn", kind, "future-service", new(""), 0, .5, [])],
        };
        var selected = new ForecastActivityOutcomeComparison(["service"], new([Allocation("service", "pawn", 0, 10)], 10, 0), [], 0, false);
        Assert.Null(CoordinatedActivityBinding.StartSelected(selected, context.Bindings, 1, "counterfactual", 100, context: context));
    }
}
