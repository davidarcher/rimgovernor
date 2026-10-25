using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastIncumbentPreparationTests
{
    private static readonly ProductionBatchCensus NoBatches = new ProductionBatchCensus.Read([]);

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
        var row = new WorkAction(
            new("joy-plan"),
            action,
            new()
            {
                Action = action.Id,
                Stage = Stage.Completed,
                Attempt = new(1),
                Receipt = new ReceiptState.Received(Receipt.Accepted),
                Tick = new(100),
            }
        );
        EquatableArray<WorkResourcePawn> native = [
            new("pawn", "joy-assignment", 1, "started", "", "", 0) { LocalHour = 12 },
        ];
        var evidence = facts.WorkView!.Evidence with
        {
            WorkResources = native,
            Pawns = EquatableArray.From(facts.WorkView.Evidence.Pawns.Select(p => p with { Occupied = true }))
        };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, [row]);
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            WorkView = view, WorkClaims = view.Claims, Census = facts.Census with { WorkResources = native },
            ForecastObserved = facts.ForecastObserved with { Needs = needs with { Pawns = [needs.Pawns[0] with
            {
                Location = new(1, 1),
                CurrentService = new("joy-assignment", "joy-method", 1, "pin", "joy", new(1, 1), 100d, .001) { Unsupported = [] },
                Needs = [.. needs.Pawns[0].Needs, NeedsFixtures.Rate("joy", .2, .0001, .3)]
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
        Assert.True(candidates.Context.Resources.Evidence.Pawns[0].Occupied == false);
        Assert.Equal(facts.WorkView!.Evidence.Stock, candidates.Context.Resources.Evidence.Stock);
        Assert.Equal(facts.WorkView.Evidence.Reservations, candidates.Context.Resources.Evidence.Reservations);
        var task = Assert.Single(candidates.Context.Activities.Activities, a => a.Id == incumbent.Commitment);
        Assert.Equal(0d, task.LatestStart);
        Assert.Equal(100d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == task.Id).WorkTicks);
        Assert.Equal(.1, Assert.Single(candidates.Context.NeedTasks, n => n.Task == task.Id).Gain, 8);
        Assert.Empty(Assert.Single(candidates.Incumbents, a => a.Id == incumbent.Commitment).ExclusiveOutcome);
        Assert.DoesNotContain(candidates.Alternatives, a => a.Id == incumbent.Commitment);
    }

    [Fact]
    public void RetainingStationaryServicePreservesObservedRoutesForLaterWork()
    {
        var (facts, actions) = Current();
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
        { Routes = [new("pawn", new(1, 1), new(5, 5), new RouteTicks.Reachable(17d)) { FromStart = true },
            new("pawn", new(8, 8), new(5, 5), new RouteTicks.Reachable(31d)) { FromStart = false }] } } };
        var (prepared, _) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var routes = prepared.CoupledAlternatives!.Context.Activities.Routes;
        Assert.Equal(new RouteTicks.Reachable(17d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/1/1" && r.To == "cell/5/5").Ticks);
        Assert.Equal(new RouteTicks.Reachable(31d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/8/8" && r.To == "cell/5/5").Ticks);
        Assert.Equal(new RouteTicks.Reachable(0d), Assert.Single(routes, r => r.Pawn == "pawn" && r.From == "cell/1/1" && r.To == "start").Ticks);
    }

    [Theory]
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
        if (reason == "revision")
            facts = facts with
            {
                ForecastObserved = facts.ForecastObserved with
                {
                    Needs = needs with
                    {
                        Pawns =
                        [
                            needs.Pawns[0] with
                            {
                                CurrentService = needs.Pawns[0].CurrentService! with
                                {
                                    Revision = 2,
                                },
                            },
                        ],
                    },
                },
            };
        if (reason == "carry")
            facts = facts with
            {
                WorkView = facts.WorkView! with
                {
                    Evidence = facts.WorkView!.Evidence with
                    {
                        WorkResources = [
                            new("pawn", "joy-assignment", 1, "started", "rice", "Rice", 1)
                            {
                                LocalHour = 12,
                            },
                        ],
                    },
                },
            };
        if (reason == "stale")
            facts = facts with { Tick = new(101) };
        if (reason == "unaccepted")
            actions = new([
                actions.Actions[0] with
                {
                    Progress = actions.Actions[0].Progress with
                    {
                        Receipt = new ReceiptState.NoReceipt(),
                    },
                },
            ]);
        if (reason == "truncated")
            actions = actions with { Truncated = true };
        if (reason == "mixed")
            actions = new([
                actions.Actions[0],
                actions.Actions[0] with
                {
                    Plan = new("other-plan"),
                },
            ]);
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

    private static (RoundsFacts Facts, WorkActionCensus Actions) FutureRest(bool tombstone = false)
    {
        var (facts, initial) = Current();
        var row = initial.Actions[0]; var batch = row.Action.PawnActivities!;
        var step = new PawnActivityStep(PawnActivityKind.Sleep, "bed", "", "", 0, 300);
        var task = new SelectedActivityTask("future-rest", "pawn", [], step, new(2, 2), 250);
        var order = batch.Orders[0] with { Task = task.Id, Preparing = true, Steps =
            [new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, 300) { NotBefore = 250 }.At(task.Location),
             new(PawnActivityKind.Hold, "", "", "", 0, 300)] };
        batch = batch with { Tasks = [task], Orders = tombstone
            ? [order, new("helper", "old-helper", "joy-method", 1, 2, true, [])] : [order] };
        row = row with { Action = DomainAction.NewPawnActivitiesAction(row.Action.Id, batch) };
        var native = new List<WorkResourcePawn> { new("pawn", order.Assignment, order.Revision, "holding", "", "", 0) { LocalHour = 12 } };
        if (tombstone) native.Add(new("helper", "new-independent-idle", 1, "holding", "", "", 0) { LocalHour = 12 });
        var evidence = facts.WorkView!.Evidence with { WorkResources = EquatableArray.From(native) };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, [row]);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            CurrentService = null, SleepThreshold = .9, WakeThreshold = 1d,
            SleepBlockedUntilTicks = 0d, SleepWhileStarving = false,
            Needs = [NeedsFixtures.Rate("food", .2, .001, .3),
                NeedsFixtures.Rate("rest", .8, .0001, .3)],
            Targets = [NeedsFixtures.Service("bed", "rest", 10d, .001) with
                { Location = task.Location }]
        };
        return (facts with { WorkView = view, WorkClaims = view.Claims, Census = facts.Census with { WorkResources = evidence.WorkResources },
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
        Assert.Equal(150d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == waiting).WorkTicks);
        Assert.Equal(40d, Assert.Single(candidates.Context.Activities.Options, o => o.Activity == incumbent.Commitment).WorkTicks);
        Assert.Equal(160d, Assert.Single(candidates.Context.Activities.Activities, a => a.Id == incumbent.Commitment).LatestStart);
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            prepared,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.True(selected.Baseline.Feasible);
        Assert.DoesNotContain(incumbent.Commitment, selected.Selection.Selected);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Activity.Contains("/eat/", StringComparison.Ordinal));
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value);
        Assert.Single(execution.Releases);
        Assert.Null(execution.Dispatch); // Replacement cannot create a second native owner before release.
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
        var native = facts.WorkView!.Evidence.WorkResources;
        facts = facts with { WorkView = facts.WorkView! with { Evidence = facts.WorkView!.Evidence with
            { WorkResources = [native[0], native[1] with { Assignment = "old-helper" }] } } };
        Assert.Empty(ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions).Incumbents);
    }

    /// <summary>"pawn" owns a coupled cooking Method holding for its next recipe; "helper" is free and cooks in
    /// <paramref name="helperTicks"/> against pawn's 100, walking <paramref name="helperApproach"/> ticks to the rice against
    /// pawn's 0; the poisoning chances are unknown when NaN. <paramref name="finished"/>: pawn finished its recipe and another
    /// is left. <paramref name="busy"/>: a second participant "busy" is mid-task on a recipe that is independent of pawn's,
    /// "depends" on pawn's next recipe, or "shares" its bill.</summary>
    private static (RoundsFacts Facts, WorkActionCensus Actions) BoundaryCooking(double helperTicks, string state = "holding",
        double helperApproach = 0, double pawnPoison = 0, double helperPoison = 0, bool finished = false, string busy = "")
    {
        var conversion = ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "CookRice", 100)
            with { AvailableInput = 8, CanSupply = true, Cooking = new FoodCooking.Cook(100, pawnPoison, null) };
        var facts = ForecastPortfolioChoiceTests.Observed([conversion, conversion with { Pawn = "helper",
                Cooking = new FoodCooking.Cook(helperTicks, helperPoison, null), ApproachTicks = helperApproach }],
            [new("rice", "Rice", new(1, 1), 8, new FoodFreshness.Rots(2000d))], helper: true);
        var coupled = facts.ForecastObserved.Coupled!;
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = coupled with { Routes = EquatableArray.From(coupled.Routes
            .Select(r => r.FromStart && r.Pawn == "helper" && r.To == new Cell(1, 1) ? r with { Ticks = new RouteTicks.Reachable(helperApproach) } : r)) } } };
        var recipe = new PawnActivityRecipe("bill/CookRice", "CookRice", [new("observed-output", 4)]);
        var task = new SelectedActivityTask("cook-task", "pawn", [], new(PawnActivityKind.Recipe, "bench", "Cook", "", 0, 2000) { Recipe = recipe }, new(2, 1), 400);
        var order = new PawnActivityOrder("pawn", "cook-assignment", "coupled-cook", 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, 2000) { NotBefore = 400 }.At(task.Location), new(PawnActivityKind.Hold, "", "", "", 0, 2000)])
            { Task = task.Id, Preparing = true };
        List<SelectedActivityTask> tasks = [task];
        if (finished)
        {
            // pawn's order is the recipe it just finished; its next recipe waits for the review.
            order = new PawnActivityOrder("pawn", "cook-assignment", "coupled-cook", 0, 1, false, [task.Step]) { Task = task.Id };
            tasks = [task, task with { Id = "cook-task-2", DepartTick = 900 }];
        }
        List<PawnActivityOrder> orders = [order];
        if (busy.Length != 0)
        {
            var other = new PawnActivityRecipe(busy == "shares" ? recipe.Bill : "bill/Other", "Other", [new("other-output", 1)]);
            var work = new SelectedActivityTask("busy-task", "busy", busy == "depends" ? ["cook-task-2"] : [],
                new(PawnActivityKind.Recipe, "bench-2", "Cook", "", 0, 2000) { Recipe = other }, new(4, 1), 100);
            tasks.Add(work);
            orders.Add(new PawnActivityOrder("busy", "busy-assignment", "coupled-cook", 0, 1, false, [work.Step]) { Task = work.Id });
        }
        var action = DomainAction.NewPawnActivitiesAction(
            new("cook-action"),
            new PawnActivities(1, orders.ToArray()) { Tasks = EquatableArray.From(tasks) }
        );
        var row = new WorkAction(
            new("cook-plan"),
            action,
            new()
            {
                Action = action.Id,
                Stage = Stage.Completed,
                Attempt = new(1),
                Receipt = new ReceiptState.Received(Receipt.Accepted),
                Tick = new(100),
            }
        );
        WorkAction[] rows = [row];
        var native = facts.WorkView!.Evidence.WorkResources.Select(p => p.Pawn == "pawn" ? p with { Assignment = order.Assignment, Revision = order.Revision, State = state } : p).ToList();
        if (busy.Length != 0) native.Add(new("busy", "busy-assignment", 1, "started", "", "", 0) { LocalHour = 12, Bill = busy == "shares" ? recipe.Bill : "bill/Other" });
        var evidence = facts.WorkView.Evidence with { WorkResources = EquatableArray.From(native) };
        var view = WorkRules.WorkResourcesOf(evidence, NoBatches, rows);
        return (facts with { WorkView = view, WorkClaims = view.Claims, Census = facts.Census with { WorkResources = evidence.WorkResources } }, new(EquatableArray.From(rows)));
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
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            prepared, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, []);
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

    [Theory]
    [InlineData(200, "holding")]    [InlineData(10, "started")]
    public void AMethodStaysProtectedWithoutAFasterFreeCookOrMidTask(double helperTicks, string state)
    {
        var (facts, actions) = BoundaryCooking(helperTicks, state);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Empty(incumbents);
        Assert.Contains("pawn", prepared.CoupledAlternatives!.Context.Resources.Claims.Pawns);
    }

    [Theory]
    // #3624: a worker that finished a recipe with another left is at a boundary (its next recipe waits for the review), so a
    // faster free cook can take it. Started, it is mid-task and protected.
    [InlineData("completed", true)]    [InlineData("started", false)]
    public void AWorkerThatFinishedATaskIsReconsideredUntilItStartsTheNext(string state, bool reconsidered)
    {
        var (facts, actions) = BoundaryCooking(10, state, finished: true);
        var (_, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Equal(reconsidered, incumbents.Count == 1);
    }

    [Theory]
    // #3624: any one worker at a boundary is reconsidered while another participant keeps working, unless that participant
    // waits on the worker's next task or holds the same bill: releasing the worker would strand it.
    [InlineData("independent", true)]    [InlineData("depends", false)]    [InlineData("shares", false)]
    public void AnyFinishedWorkerIsReconsideredWhileAnIndependentParticipantWorks(string busy, bool reconsidered)
    {
        var (facts, actions) = BoundaryCooking(10, "completed", finished: true, busy: busy);
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        Assert.Equal(reconsidered, incumbents.Count == 1);
        var claims = prepared.CoupledAlternatives!.Context.Resources.Claims.Pawns;
        Assert.Contains("busy", claims);
        if (!reconsidered) return;
        Assert.Equal(["pawn"], incumbents[0].Reconsidered.ToArray());
        Assert.Equal("busy", Assert.Single(incumbents[0].Working).Pawn);
        Assert.DoesNotContain("pawn", claims);
    }

    [Fact]
    public void AReconsideredWorkerAloneIsReleasedAndTheWorkingParticipantKeepsItsOrderAndTasks()
    {
        // #3624: the review re-decides only the finished worker's next task; the Method is not released as a whole.
        var (facts, actions) = BoundaryCooking(10, "completed", finished: true, busy: "independent");
        var (prepared, incumbents) = ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions);
        var candidates = prepared.CoupledAlternatives!.Priced();
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            prepared, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(prepared) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Pawn == "helper" && candidates.Context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && b.Kind == PawnActivityKind.Recipe);
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        var release = Assert.Single(execution.Releases).Batch;
        var pawn = Assert.Single(release.Orders, o => o.Pawn == "pawn");
        Assert.True(pawn.Release);
        Assert.Equal((1L, 2L), (pawn.ExpectedRevision, pawn.Revision));
        var before = actions.Actions[0].Action.PawnActivities!;
        Assert.Equal(Assert.Single(before.Orders, o => o.Pawn == "busy"), Assert.Single(release.Orders, o => o.Pawn == "busy"));
        Assert.Equal(["busy-task"], release.Tasks.Select(t => t.Id).ToArray());
    }

    [Theory]
    // #3167: a cook 90 work ticks faster but 200 ticks further from the rice is slower in total; 50 ticks away it is faster.
    [InlineData(200, false)]    [InlineData(50, true)]
    public void TheFasterOutsideGateCountsTravelToTheIngredients(double helperApproach, bool reconsidered)
    {
        var (facts, actions) = BoundaryCooking(10, helperApproach: helperApproach);
        Assert.Equal(reconsidered, ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions).Incumbents.Count == 1);
    }

    [Theory]
    // #3167: an equally fast cook is preferred only with a lower chance its meal is poisoned.
    [InlineData(0.05, 0.01, true)]    [InlineData(0.05, 0.05, false)]    [InlineData(0.01, 0.05, false)]
    public void AnEquallyFastCookWithALowerPoisoningChanceReconsidersTheMethod(double pawnPoison, double helperPoison, bool reconsidered)
    {
        var (facts, actions) = BoundaryCooking(100, pawnPoison: pawnPoison, helperPoison: helperPoison);
        Assert.Equal(reconsidered, ForecastIncumbentPreparation.PrepareCoupledIncumbents(facts, NoBatches, actions).Incumbents.Count == 1);
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
            needs.Add(facts.ForecastObserved.Needs!.Pawns[0] with { Pawn = pawn, CurrentService = new(order.Assignment, order.Method, 1, "pin", "joy", new(1, 1), 100d, .001) { Unsupported = [] } });
            native.Add(new(pawn, order.Assignment, 1, "started", "", "", 0) { LocalHour = 12 });
            capacity.Add(facts.WorkView!.Evidence.Pawns[0] with { Pawn = pawn });
        }
        var evidence = facts.WorkView!.Evidence with { Pawns = EquatableArray.From(capacity), WorkResources = EquatableArray.From(native) };
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
