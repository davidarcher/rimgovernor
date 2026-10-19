using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastIncumbentPreparationTests
{
    private static readonly Fact<EquatableArray<ProductionBatch>> NoBatches = Known<EquatableArray<ProductionBatch>>([]);

    [Fact]
    public void OrdinaryGenerationStillRunsWithoutAnyEligibleIncumbent()
    {
        var facts = ForecastCoupledSelectionTests.Review() with { CoupledAlternatives = null };
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, new([]));
        Assert.Empty(incumbents);
        Assert.NotEmpty(Assert.IsType<ForecastCoupledCandidates>(prepared.CoupledAlternatives).Alternatives);
        Assert.Same(facts.WorkView, prepared.WorkView);
    }

    private static (RoundsFacts Facts, WorkActionCensus Actions) Current()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        var step = new PawnActivityStep(PawnActivityKind.Recreation, "pin", "", "", 0, 1000);
        var task = new SelectedActivityTask("joy-task", "pawn", [], step, new(1, 1), 100);
        var batch = new PawnActivities(1, [new("pawn", "joy-assignment", "joy-method", 0, 1, false, [step]) { Task = task.Id }]) { Tasks = [task] };
        var action = DomainAction.NewPawnActivitiesAction(new("joy-action"), batch);
        var row = new WorkAction(new("joy-plan"), action, new() { Action = action.Id, Stage = Stage.Completed,
            Attempt = new(1), Receipt = Known(Receipt.Accepted), Tick = new(100) });
        var native = Known<EquatableArray<WorkResourcePawn>>([new("pawn", "joy-assignment", 1, "started", "", "", 0)]);
        var evidence = facts.WorkView!.Evidence with
        {
            WorkResources = native,
            Pawns = Known(EquatableArray.From(facts.WorkView.Evidence.Pawns.Value.Select(p => p with { Occupied = Known(true) })))
        };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, [row]);
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            WorkView = view, WorkClaims = view.Claims, WorkResources = native,
            ForecastObserved = facts.ForecastObserved with { Needs = needs with { Pawns = [needs.Pawns[0] with
            {
                Location = new(1, 1),
                CurrentService = new("joy-assignment", "joy-method", 1, "pin", "joy", new(1, 1), Known(100d), Known(.001)),
                Needs = [.. needs.Pawns[0].Needs, new("joy", Known(.2), Known(.0001), Known(.3))]
            }] } }
        };
        return (facts, new([row]));
    }

    [Fact]
    public void ExactInstalledServiceIsAConditionalAlternativeWithoutReleasingAdmissionClaims()
    {
        var (facts, actions) = Current();
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var incumbent = Assert.Single(incumbents);
        Assert.Same(facts.WorkView, prepared.WorkView);
        Assert.Contains("pawn", prepared.WorkView!.Claims.Pawns);
        var candidates = Assert.IsType<ForecastCoupledCandidates>(prepared.CoupledAlternatives);
        Assert.DoesNotContain("pawn", candidates.Context.Resources.Claims.Pawns);
        Assert.True(candidates.Context.Resources.Evidence.Pawns.Value[0].Occupied == Known(false));
        Assert.Equal(facts.WorkView!.Evidence.Stock, candidates.Context.Resources.Evidence.Stock);
        Assert.Equal(facts.WorkView.Evidence.Reservations, candidates.Context.Resources.Evidence.Reservations);
        var task = Assert.Single(candidates.Context.Activities.Activities, a => a.Id == incumbent.Commitment);
        Assert.Equal(0d, task.LatestStart);
        Assert.Equal(100d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == task.Id).WorkTicks.Value);
        Assert.Equal(.1, Assert.Single(candidates.Context.NeedTasks, n => n.Task == task.Id).Gain.Value, 8);
        Assert.Empty(Assert.Single(candidates.Incumbents, a => a.Id == incumbent.Commitment).ExclusiveOutcome);
        Assert.DoesNotContain(candidates.Alternatives, a => a.Id == incumbent.Commitment);
    }

    [Fact]
    public void RetainingStationaryServicePreservesObservedRoutesForLaterWork()
    {
        var (facts, actions) = Current();
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
        { Routes = [new("pawn", new(1, 1), new(5, 5), Known(17d)) { FromStart = true },
            new("pawn", new(8, 8), new(5, 5), Known(31d))] } } };
        var (prepared, _) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var routes = prepared.CoupledAlternatives!.Context.Activities.Routes;
        Assert.Equal(Known(17d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/1/1" && r.To == "cell/5/5").Ticks);
        Assert.Equal(Known(31d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/8/8" && r.To == "cell/5/5").Ticks);
        Assert.Equal(Known(0d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/1/1" && r.To == "start").Ticks);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("revision")]
    [InlineData("carry")]
    [InlineData("stale")]
    [InlineData("unaccepted")]
    [InlineData("truncated")]
    [InlineData("mixed")]
    [InlineData("input")]
    [InlineData("output")]
    public void UncertainOrMixedOwnershipRemainsProtected(string reason)
    {
        var (facts, actions) = Current();
        var needs = facts.ForecastObserved.Needs!;
        if (reason is "unknown" or "revision")
            facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = needs with { Pawns = [needs.Pawns[0] with
            { CurrentService = needs.Pawns[0].CurrentService! with { RemainingTicks = reason == "unknown" ? default : Known(100d), Revision = reason == "revision" ? 2 : 1 } }] } } };
        if (reason == "carry") facts = facts with { WorkView = facts.WorkView! with { Evidence = facts.WorkView!.Evidence with
            { WorkResources = Known<EquatableArray<WorkResourcePawn>>([new("pawn", "joy-assignment", 1, "started", "rice", "Rice", 1)]) } } };
        if (reason == "stale") facts = facts with { Tick = new(101) };
        if (reason == "unaccepted") actions = new([actions.Actions[0] with { Progress = actions.Actions[0].Progress with { Receipt = default } }]);
        if (reason == "truncated") actions = actions with { Truncated = true };
        if (reason == "mixed") actions = new([actions.Actions[0], actions.Actions[0] with { Plan = new("other-plan") }]);
        if (reason is "input" or "output")
        {
            var row = actions.Actions[0]; var batch = row.Action.PawnActivities!;
            batch = reason == "input" ? batch with { Tasks = [batch.Tasks[0] with { Inputs = [new("Rice", "rice", 1, new(1, 1), "observed") { Observed = true }] }] }
                : batch with { Orders = [batch.Orders[0] with { OutputDelivery = new("Rice", new(1, 1), new(2, 2), 1, 1000, 1) }] };
            actions = new([row with { Action = DomainAction.NewPawnActivitiesAction(row.Action.Id, batch) }]);
        }
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Empty(incumbents);
        Assert.Same(facts.WorkView, prepared.WorkView);
        Assert.Contains("pawn", prepared.WorkView!.Claims.Pawns);
    }

    private static (RoundsFacts Facts, WorkActionCensus Actions) FutureRest(bool knownRoute = true, bool tombstone = false)
    {
        var (facts, initial) = Current();
        var row = initial.Actions[0]; var batch = row.Action.PawnActivities!;
        var step = new PawnActivityStep(PawnActivityKind.Sleep, "bed", "", "", 0, 300);
        var task = new SelectedActivityTask("future-rest", "pawn", [], step, new(2, 2), 250);
        var order = batch.Orders[0] with { Task = task.Id, Preparing = true, Steps =
            [new(PawnActivityKind.Approach, "", "", "", 0, 300) { Destination = task.Location, NotBefore = 250 },
             new(PawnActivityKind.Hold, "", "", "", 0, 300)] };
        batch = batch with { Tasks = [task], Orders = tombstone
            ? [order, new("helper", "old-helper", "joy-method", 1, 2, true, [])] : [order] };
        row = row with { Action = DomainAction.NewPawnActivitiesAction(row.Action.Id, batch) };
        var native = new List<WorkResourcePawn> { new("pawn", order.Assignment, order.Revision, "holding", "", "", 0) };
        if (tombstone) native.Add(new("helper", "new-independent-idle", 1, "holding", "", "", 0));
        var evidence = facts.WorkView!.Evidence with { WorkResources = Known(EquatableArray.From(native)) };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, [row]);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            CurrentService = null, SleepThreshold = Known(.9), WakeThreshold = Known(1d),
            SleepBlockedUntilTicks = Known(0d), SleepWhileStarving = Known(false),
            Needs = [new("food", Known(.2), Known(.001), Known(.3)),
                new("rest", Known(.8), Known(.0001), Known(.3))],
            Targets = [new("bed", "rest", "", 1, knownRoute ? Known(10d) : default, default, default, Known(.001))
                { Location = task.Location }]
        };
        return (facts with { WorkView = view, WorkClaims = view.Claims, WorkResources = evidence.WorkResources,
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs! with { Pawns = [pawn] } } }, new([row]));
    }

    [Fact]
    public void FutureHoldCompetesWithFoodUsingOccupiedWaitAndFreshServiceMechanics()
    {
        var (facts, actions) = FutureRest();
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var incumbent = Assert.Single(incumbents);
        var candidates = prepared.CoupledAlternatives!.Priced();
        Assert.Equal(2, incumbent.Activities.Count);
        var waiting = Assert.Single(candidates.Context.NonproductiveOccupancy);
        Assert.DoesNotContain(candidates.Context.NeedTasks, n => n.Task == waiting);
        Assert.Equal(150d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == waiting).WorkTicks.Value);
        Assert.Equal(40d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == incumbent.Commitment).WorkTicks.Value);
        Assert.Equal(160d, Assert.Single(candidates.Context.Activities.Activities, a => a.Id == incumbent.Commitment).LatestStart);
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            prepared,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Equal(Known(true), selected.Baseline.Feasible);
        Assert.DoesNotContain(incumbent.Commitment, selected.Selection.Selected);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Activity.Contains("/eat/", StringComparison.Ordinal));
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value);
        Assert.Single(execution.Releases);
        Assert.Null(execution.Dispatch); // Replacement cannot create a second native owner before release.
        Assert.Same(facts.WorkView, prepared.WorkView);
        Assert.Contains("pawn", prepared.WorkView!.Claims.Pawns);
    }

    [Fact]
    public void UnknownFutureRouteKeepsTheExistingOwnerProtected()
    {
        var (facts, actions) = FutureRest(knownRoute: false);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Empty(incumbents);
        Assert.Same(facts.WorkView, prepared.WorkView);
        Assert.Contains("pawn", prepared.WorkView!.Claims.Pawns);
    }

    [Fact]
    public void ReleasedHelperWithNewIndependentOwnerDoesNotBlockFutureHoldComparison()
    {
        var (facts, actions) = FutureRest(tombstone: true);
        var (_, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var released = Assert.Single(Assert.Single(incumbents).ReleasedOwnership);
        Assert.Equal("new-independent-idle", released.Assignment);
        var native = facts.WorkView!.Evidence.WorkResources.Value;
        facts = facts with { WorkView = facts.WorkView! with { Evidence = facts.WorkView!.Evidence with
            { WorkResources = Known<EquatableArray<WorkResourcePawn>>([native[0], native[1] with { Assignment = "old-helper" }]) } } };
        Assert.Empty(ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions).Incumbents);
    }

    /// <summary>"pawn" owns a coupled cooking Method holding for its next recipe; "helper" is free and cooks in
    /// <paramref name="helperTicks"/> against pawn's 100.</summary>
    private static (RoundsFacts Facts, WorkActionCensus Actions) BoundaryCooking(double helperTicks, string state = "holding", bool deferHelper = false)
    {
        var conversion = ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "CookRice", 100) with { AvailableInput = 8, CanSupply = true, CanDeliver = true };
        var facts = ForecastPortfolioChoiceTests.Observed([conversion, conversion with { Pawn = "helper", WorkTicks = Known(helperTicks) }],
            [new("rice", "Rice", new(1, 1), 8, Known(2000d))], helper: true);
        var recipe = new PawnActivityRecipe("bill/CookRice", "CookRice", [new("observed-output", 4)]);
        var task = new SelectedActivityTask("cook-task", "pawn", [], new(PawnActivityKind.Recipe, "bench", "Cook", "", 0, 2000) { Recipe = recipe }, new(2, 1), 400);
        var order = new PawnActivityOrder("pawn", "cook-assignment", "coupled-cook", 0, 1, false,
            [new(PawnActivityKind.Approach, "", "", "", 0, 2000) { Destination = task.Location, NotBefore = 400 }, new(PawnActivityKind.Hold, "", "", "", 0, 2000)])
            { Task = task.Id, Preparing = true };
        var action = DomainAction.NewPawnActivitiesAction(new("cook-action"), new PawnActivities(1, [order]) { Tasks = [task] });
        var row = new WorkAction(new("cook-plan"), action, new() { Action = action.Id, Stage = Stage.Completed,
            Attempt = new(1), Receipt = Known(Receipt.Accepted), Tick = new(100) });
        WorkAction[] rows = [row];
        // The helper's own coupled Method only defers it (#3047) until its distant service is due.
        var defer = new SelectedActivityTask("defer/helper", "helper", [], new(PawnActivityKind.Idle, "", "", "", 0, 5000), new(1, 1), 100);
        var idle = new PawnActivityOrder("helper", "defer-assignment", "coupled-defer", 0, 1, false, [defer.Step]) { Task = defer.Id };
        if (deferHelper)
        {
            var deferral = DomainAction.NewPawnActivitiesAction(new("defer-action"), new PawnActivities(1, [idle]) { Tasks = [defer] });
            rows = [row, new WorkAction(new("defer-plan"), deferral, new() { Action = deferral.Id, Stage = Stage.Completed,
                Attempt = new(1), Receipt = Known(Receipt.Accepted), Tick = new(100) })];
        }
        var native = facts.WorkView!.Evidence.WorkResources.Value.Select(p => p.Pawn == "pawn" ? p with { Assignment = order.Assignment, Revision = 1, State = state }
            : deferHelper && p.Pawn == "helper" ? p with { Assignment = idle.Assignment, Revision = 1, State = "started" } : p).ToArray();
        var evidence = facts.WorkView.Evidence with { WorkResources = Known(EquatableArray.From(native)) };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, rows);
        return (facts with { WorkView = view, WorkClaims = view.Claims, WorkResources = evidence.WorkResources }, new(EquatableArray.From(rows)));
    }

    [Fact]
    public void ABoundaryMethodIsReconsideredAndReleasedWhenAFasterFreeCookTakesItsBill()
    {
        // #3167: a better cook joined, but the first cook's Method stayed protected and its pawns' capacity unknown.
        var (facts, actions) = BoundaryCooking(10);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var incumbent = Assert.Single(incumbents);
        Assert.Equal(["bill/CookRice"], incumbent.Bills.ToArray());
        var candidates = Assert.IsType<ForecastCoupledCandidates>(prepared.CoupledAlternatives).Priced();
        Assert.DoesNotContain("pawn", candidates.Context.Resources.Claims.Pawns);
        Assert.DoesNotContain("unknown_needs_capacity", candidates.Findings);
        Assert.Empty(candidates.Incumbents); // No retain commitment: the comparison sees both cooks free.
        Assert.Contains("pawn", prepared.WorkView!.Claims.Pawns); // Admission claims stay until the release is observed.
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            prepared, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Pawn == "helper" && candidates.Context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Kind == PawnActivityKind.Recipe);
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        var release = Assert.Single(execution.Releases);
        Assert.True(Assert.Single(release.Batch.Orders).Release);
        var dispatch = Assert.IsType<ForecastCoupledSelection>(execution.Dispatch);
        Assert.DoesNotContain(dispatch.Selection.Plan!.Allocations, a => a.Pawn == "pawn");
        Assert.Contains(dispatch.Selection.Plan!.Allocations, a => a.Pawn == "helper");
        // The same selection without the move keeps the Method: its owner stays and only the rest is dispatched.
        var kept = ForecastCoupledExecution.ReviewCoupledExecution(selected with { Selection = selected.Selection with { Plan = selected.Selection.Plan! with
            { Allocations = selected.Selection.Plan!.Allocations.Where(a => a.Pawn != "helper").ToArray() } } }, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        Assert.Empty(kept.Releases);
    }

    [Fact]
    public void ADeferredColonistBesideAReconsideredMethodIsReleasedWhenTheSelectionGivesItWork()
    {
        // #3167: development-reassignment's free colonists were deferred, so a better cook staged on one stayed owned.
        var (facts, actions) = BoundaryCooking(10, deferHelper: true);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Equal(2, incumbents.Count);
        var deferral = Assert.Single(incumbents, i => i.Deferral);
        Assert.Equal("helper", deferral.Ownership.Pawn);
        var candidates = Assert.IsType<ForecastCoupledCandidates>(prepared.CoupledAlternatives).Priced();
        Assert.DoesNotContain("helper", candidates.Context.Resources.Claims.Pawns);
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            prepared, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Pawn == "helper");
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        Assert.Equal(2, execution.Releases.Count);
        Assert.Contains(execution.Releases, r => r.Incumbent.Deferral && r.Batch.Orders.Single().Release);
        // Without work for it the deferral stands.
        var idle = ForecastCoupledExecution.ReviewCoupledExecution(selected with { Selection = selected.Selection with { Plan = selected.Selection.Plan! with
            { Allocations = selected.Selection.Plan!.Allocations.Where(a => a.Pawn != "helper").ToArray() } } }, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        Assert.Empty(idle.Releases);
    }

    [Theory]
    [InlineData(200, "holding")]    [InlineData(10, "started")]
    public void AMethodStaysProtectedWithoutAFasterFreeCookOrMidTask(double helperTicks, string state)
    {
        var (facts, actions) = BoundaryCooking(helperTicks, state);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Empty(incumbents);
        Assert.Contains("pawn", prepared.CoupledAlternatives!.Context.Resources.Claims.Pawns);
    }

    [Fact]
    public void FourOwnerBoundLeavesUnvisitedOwnerProtectedAndReportsTruncation()
    {
        var (facts, initial) = Current();
        var rows = new List<WorkAction>(); var needs = new List<NeedsForecastPawn>(); var native = new List<WorkResourcePawn>();
        var capacity = new List<WorkPawnAvailability>();
        for (var i = 0; i < 5; i++)
        {
            var pawn = "pawn" + i;
            var original = initial.Actions[0]; var batch = original.Action.PawnActivities!;
            var order = batch.Orders[0] with { Pawn = pawn, Assignment = "assignment" + i, Method = "method" + i };
            batch = batch with { Orders = [order], Tasks = [batch.Tasks[0] with { Pawn = pawn }] };
            var action = DomainAction.NewPawnActivitiesAction(new("action" + i), batch);
            rows.Add(new(new("plan" + i), action, original.Progress with { Action = action.Id }));
            needs.Add(facts.ForecastObserved.Needs!.Pawns[0] with { Pawn = pawn, CurrentService = new(order.Assignment, order.Method, 1, "pin", "joy", new(1, 1), Known(100d), Known(.001)) });
            native.Add(new(pawn, order.Assignment, 1, "started", "", "", 0));
            capacity.Add(facts.WorkView!.Evidence.Pawns.Value[0] with { Pawn = pawn });
        }
        var evidence = facts.WorkView!.Evidence with { Pawns = Known(EquatableArray.From(capacity)), WorkResources = Known(EquatableArray.From(native)) };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, rows);
        facts = facts with { WorkView = view, WorkClaims = view.Claims, ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(needs) },
            Coupled = facts.ForecastObserved.Coupled! with { Food = facts.ForecastObserved.Coupled!.Food with { Options = [] }, FoodUses = [] }
        } };
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, new(EquatableArray.From(rows)));
        Assert.Equal(4, incumbents.Count);
        Assert.True(prepared.CoupledAlternatives!.Truncated);
        Assert.Contains("incumbent_preparation_truncated:protected_remaining_owners", prepared.CoupledAlternatives.Findings);
        Assert.Contains("pawn4", prepared.CoupledAlternatives.Context.Resources.Claims.Pawns);
        Assert.Equal(5, prepared.WorkView!.Claims.Pawns.Count);
    }
}
