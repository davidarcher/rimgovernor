using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
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

    private static (ReviewFacts Facts, WorkActionCensus Actions) Current()
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
            WorkView = view, Census = facts.Census with { WorkResources = native },
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
    [InlineData("native")]
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
        if (reason == "native")
            facts = facts with { ForecastObserved = facts.ForecastObserved with
            { Needs = needs with { Pawns = [needs.Pawns[0] with
                { CurrentService = needs.Pawns[0].CurrentService! with { Assignment = "", Method = "", Revision = 0 } }] } } };
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

    private static (ReviewFacts Facts, WorkActionCensus Actions) FutureRest(bool tombstone = false)
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
            CurrentService = null, ServedNeed = "", SleepThreshold = .9, WakeThreshold = 1d,
            SleepBlockedUntilTicks = 0d, SleepWhileStarving = false,
            Needs = [NeedsFixtures.Rate("food", .2, .001, .3),
                NeedsFixtures.Rate("rest", .8, .0001, .3)],
            Targets = [NeedsFixtures.Service("bed", "rest", 10d, .001) with
                { Location = task.Location }]
        };
        return (facts with { WorkView = view, Census = facts.Census with { WorkResources = evidence.WorkResources },
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs! with { Pawns = [pawn] } } }, new([row]));
    }

    [Fact]
    public void ConditionalFutureHoldCompetesWithFoodWithoutAuthorizingRelease()
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
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates, basis: ForecastComparisonBasis.ConditionalScenario);
        Assert.True(selected.Baseline.Feasible);
        Assert.DoesNotContain(incumbent.Commitment, selected.Selection.Selected);
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Activity.Contains("/eat/", StringComparison.Ordinal));
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selected, incumbents, facts.Tick.Value);
        Assert.Empty(execution.Releases);
        Assert.Null(execution.Dispatch); // A conditional ranking cannot replace a native owner.
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
        facts = facts with { WorkView = view, ForecastObserved = facts.ForecastObserved with
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
