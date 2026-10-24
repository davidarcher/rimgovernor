using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

// #3194: a Method eating one unit now and fifteen later from a 20-unit stack holds the units it still owes; the observed
// remainder stays proposable and admissible for another Method, by observed count and the journal's remaining tasks.
public sealed class SharedStackRemainderTests
{
    private const string Stack = "rice-stack";
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static readonly RoundsPolicy Policy = RoundsRules.DefaultRoundsPolicy();

    private static PawnActivityStep Eat(int count) => new(PawnActivityKind.Eat, Stack, "", "", count, 20000);
    private static SelectedActivityTask EatTask(string id, string pawn, int count, long depart) => new(id, pawn, [], Eat(count), new(2, 2), depart)
    { Resource = "Rice", Source = new(2, 2), Inputs = [new("Rice", Stack, count, new(2, 2), "observed need target") { Observed = true }] };
    /// <summary>The observed state: "pawn" eating one unit now and owing fifteen later.</summary>
    private static PawnActivities EatNowAndLater() => new(1, [new("pawn", "coupled/pawn", "coupled", 0, 1, false, [Eat(1)]) { Task = "eat-now" }])
    { Tasks = [EatTask("eat-now", "pawn", 1, 100), EatTask("eat-later", "pawn", 15, 700)] };
    private static PlanSpec Plan(string id, PawnActivities batch) => new(new(id), new(1), [DomainAction.NewPawnActivitiesAction(new(id + "-action"), batch)]);
    private static PlanSpec HelperEats(int count) => Plan("helper-" + count, new PawnActivities(1,
        [new("helper", "coupled-helper/helper", "coupled-helper", 0, 1, false, [Eat(count)]) { Task = "helper-eat" }])
        { Tasks = [EatTask("helper-eat", "helper", count, 200)] });

    private static RoundsFacts Facts(double stock, long tick = 100, params WorkResourcePawn[] native)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .2, fall: .003);
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        NeedsForecastPawn Hungry(string id) => facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Pawn = id, Location = new(1, 1),
            Targets = [NeedsFixtures.Food(Stack, "Rice", 20, 0d, 10d, .05) with
                { Location = new(2, 2), StockCount = Known(stock), MaxPawns = Known(10), FreshTicks = Known(100000d) }],
        };
        return facts with
        {
            Tick = new(tick),
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), (long)stock)]),
            WorkResources = native.Length == 0 ? roster.WorkResources : Known(EquatableArray.From(native)),
            CoupledAlternatives = null,
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = facts.ForecastObserved.Needs! with { Tick = new(tick), Pawns = [Hungry("pawn"), Hungry("helper")] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Tick = tick, Food = new(), FoodUses = [],
                    Routes = [new("pawn", new(1, 1), new(2, 2), Known(0d)) { FromStart = true }, new("helper", new(1, 1), new(2, 2), Known(0d)) { FromStart = true }],
                },
            },
        };
    }
    private static RoundsFacts Held(RoundsFacts facts, params PawnActivities[] batches) => WorkRules.ReviewWorkResources(facts,
        Known<EquatableArray<ProductionBatch>>([]), new(EquatableArray.From(batches.Select((b, i) => new WorkAction(new("held-" + i),
            DomainAction.NewPawnActivitiesAction(new("held-" + i), b), new() { Action = new("held-" + i), Stage = Stage.Pending })))));
    private static ForecastNeedTask[] StackTasks(RoundsFacts reviewed, string pawn) =>
        ForecastCoupledCandidates.GenerateCoupledActivities(reviewed, GameTime.TicksPerDay)!.Context.NeedTasks.Where(n => n.Target == Stack && n.Pawn == pawn).ToArray();
    private static int Owed(WorkResourceView view, string? owner = null) =>
        view.Claims.Shared.Where(s => s.Target == Stack && (owner is null || s.Owner == owner)).Sum(s => s.Count);

    private static ActivityTaskRead Observe(PawnActivities batch, string state, long tick, params ActivityTaskBinding[] bindings) => ConstructionFixtures.NoTasks() with
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = tick },
        Bindings = EquatableArray.From(bindings),
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, state, "", tick)))),
    };
    private static ActivityOwnership[] Owners(PawnActivities batch, string state) =>
        batch.Orders.Select(o => new ActivityOwnership(o.Pawn, o.Assignment, o.Method, o.Revision, state)).ToArray();
    private static void Send(Journal journal, PlanSpec plan, long tick, CancellationToken ct, Receipt receipt = Receipt.Accepted)
    {
        var scope = Root with { Plan = plan.Id };
        var action = Assert.Single(journal.LoadPlan(plan.Id, ct).Spec.Actions);
        journal.Prepare(plan.Id, action.Id, scope, new(tick), ct);
        var dispatched = journal.Dispatch(plan.Id, action.Id, scope, new(tick), ct);
        journal.RecordReceipt(plan.Id, action.Id, dispatched.View.Attempt, receipt, ct);
    }
    private static string Open() => Path.Combine(Path.GetTempPath(), "rg-shared-stack-" + Guid.NewGuid().ToString("N") + ".db");

    /// <summary>Installs the first Method, observes its first bite completed and continues to the owed later bite.</summary>
    private static (PlanSpec First, PlanSpec Second, PawnActivities Batch, PawnActivities Next, Rounds Review) Install(Journal journal, CancellationToken ct, Receipt continuation = Receipt.Accepted)
    {
        IntentActionRules.RegisterIntentKinds();
        var reviewed = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true, Policy = Policy, Facts = Facts(20) }, ct);
        var batch = EatNowAndLater();
        var first = Plan("first", batch);
        TestNeed.Commit(journal, Root, new("coupled"), first, ct);
        TestNeed.File(journal, met: false, new("coupled"), ct);
        Send(journal, first, 100, ct);
        // The owed count is the two tasks' units, counted once across the task row and its order.
        Assert.Equal(16, Owed(journal.WorkResources(Root, ct)));
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Observe(batch, "completed", 200))!;
        var order = Assert.Single(next.Orders);
        Assert.True(order.Preparing);
        Assert.Equal("eat-later", Assert.Single(next.Tasks).Id);
        var second = Plan("second", next);
        Assert.Equal(ActivityContinuationAdmission.Admitted, journal.ContinueActivityMethod(first.Id, Root, new(200), Owners(batch, "completed"), second, ct));
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = second.Id }, ct);
        Send(journal, second, 200, ct, continuation);
        return (first, second, batch, next, reviewed.Review);
    }

    [Fact]
    public void ObservedRemainderOfAnOwnedStackIsProposableAndAdmissibleForAnotherMethod()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Open(), ct);
        var (_, second, _, next, review) = Install(journal, ct);
        var holding = Assert.Single(next.Orders);
        // Nineteen observed units less the fifteen still owed leave four units and nine claimant slots.
        var facts = Facts(19, 200, new WorkResourcePawn("pawn", holding.Assignment, holding.Revision, "holding", "", "", 0) { LocalHour = 12 },
            new WorkResourcePawn("helper", "", 0, "", "", "", 0) { LocalHour = 12 }) with { CoupledWorkActive = true };
        journal.ReviewRounds(new RoundsRequest { Revision = review.Revision, Current = Root, Tick = new(200), Enabled = true, Policy = Policy, Facts = facts }, ct);
        var view = journal.WorkResources(Root, ct);
        Assert.Equal(15, Owed(view));
        var reviewed = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), journal.WorkActions(Root, ct));
        var bites = StackTasks(reviewed, "helper");
        Assert.NotEmpty(bites);
        Assert.Contains(bites, b => b.Count == 4);
        Assert.All(bites, b => { Assert.InRange(b.Count, 1, 4); Assert.Equal(Known(4d), b.AvailableCount); Assert.Equal(Known(9), b.MaxPawns); });
        Assert.Null(WorkRules.WorkResourcesRefusal(view, HelperEats(4)));
        Assert.Equal("target_claimed:" + Stack, WorkRules.WorkResourcesRefusal(view, HelperEats(5)));
        var helper = HelperEats(4);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(TestNeed.Id, Root, new("coupled-helper"), "", helper, ct).Outcome);
        Send(journal, helper, 200, ct);
        Assert.Equal(2, journal.ActivityMethods(Root, ct).Count);
        var shared = journal.WorkResources(Root, ct);
        Assert.Equal(15, Owed(shared, "pawn"));
        Assert.Equal(4, Owed(shared, "helper"));
        Assert.False(journal.LoadPlan(second.Id, ct).Retired);
    }

    [Fact]
    public void OwedCountEqualToTheStackLeavesNothingAndALargerObservedStackLeavesMoreWithoutChangingTheClaim()
    {
        var owned = EatNowAndLater();
        var full = Held(Facts(16), owned);
        Assert.Equal(16, Owed(full.WorkView!));
        Assert.Empty(StackTasks(full, "helper"));
        Assert.Equal("target_claimed:" + Stack, WorkRules.WorkResourcesRefusal(full.WorkView!, HelperEats(1)));
        var grown = Held(Facts(30), owned);
        Assert.Equal(16, Owed(grown.WorkView!));
        var bites = StackTasks(grown, "helper");
        Assert.Contains(bites, b => b.Count == 14);
        Assert.All(bites, b => { Assert.InRange(b.Count, 1, 14); Assert.Equal(Known(14d), b.AvailableCount); });
        Assert.Null(WorkRules.WorkResourcesRefusal(grown.WorkView!, HelperEats(14)));
        Assert.Equal("target_claimed:" + Stack, WorkRules.WorkResourcesRefusal(grown.WorkView!, HelperEats(15)));
    }

    [Fact]
    public void AStackTakenWholeOrOfUnknownCountOrCapacityStaysExcludedAndABedStaysWhole()
    {
        var target = Facts(20).ForecastObserved.Needs!.Pawns[0].Targets[0];
        var claims = Held(Facts(20), EatNowAndLater()).WorkView!.Claims;
        Assert.Equal(4, WorkRules.ProposableNeedTarget(claims, target)!.AvailableCount);
        Assert.Null(WorkRules.ProposableNeedTarget(claims, target with { StockCount = default }));
        Assert.Null(WorkRules.ProposableNeedTarget(claims, target with { MaxPawns = default }));
        Assert.Null(WorkRules.ProposableNeedTarget(claims, target with { MaxPawns = Known(1) }));
        Assert.Null(WorkRules.ProposableNeedTarget(claims, target with { Kind = "rest", StockCount = Known(2d), MaxPawns = Known(2) }));
        var unclaimed = target with { Id = "other-stack" };
        Assert.Equal(unclaimed, WorkRules.ProposableNeedTarget(claims, unclaimed));
        var hauled = Held(Facts(20), new PawnActivities(1, [new("pawn", "haul/pawn", "haul", 0, 1, false,
            [new(PawnActivityKind.Deliver, Stack, "", "", 2, 20000) { Destination = new(3, 3) }])]));
        Assert.Contains(Stack, hauled.WorkView!.Claims.Targets);
        Assert.Null(WorkRules.ProposableNeedTarget(hauled.WorkView.Claims, target));
        Assert.Empty(StackTasks(hauled, "helper"));
    }

    [Fact]
    public void ServiceBindsOnlyTheOwedCountAndAShrunkSplitMergedOrMovedStackReleases()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Open(), ct);
        var (_, _, _, next, _) = Install(journal, ct);
        var later = Assert.Single(next.Tasks);
        var bound = CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true), Observe(next, "holding", later.DepartTick, new ActivityTaskBinding.Bound(later.Id, Eat(15))))!;
        var order = Assert.Single(bound.Orders);
        Assert.False(order.Release);
        Assert.Equal(Eat(15), Assert.Single(order.Steps));
        // Native binds an Eat only to the observed lot holding at least the owed count: a stack that shrank below it, or
        // that was split, merged or moved into another thing, yields no binding; a short binding is malformed. Both release.
        Assert.True(Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true), Observe(next, "holding", later.DepartTick))!.Orders).Release);
        Assert.True(Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, Known(true),
            Observe(next, "holding", later.DepartTick, new ActivityTaskBinding.Bound(later.Id, Eat(14))))!.Orders).Release);
    }

    [Fact]
    public void RefusedContinuationKeepsThePredecessorsClaimWithoutInflatingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Open(), ct);
        var (first, second, _, _, _) = Install(journal, ct, Receipt.Refused);
        Assert.Equal(16, Owed(journal.WorkResources(Root, ct)));
        var method = Assert.Single(journal.ActivityMethods(Root, ct));
        Assert.Equal(second.Id, method.Plan);
        Assert.Equal(2, method.Batch.Tasks.Count);
        Assert.True(journal.LoadPlan(first.Id, ct).Retired);
    }

    [Fact]
    public void ReopenedJournalKeepsTheOwedClaimAndRefusesToReplayTheSupersededBite()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Open();
        PlanSpec first; PlanSpec second; PawnActivities batch;
        using (var journal = Journal.Open(path, ct)) (first, second, batch, _, _) = Install(journal, ct);
        using var reopened = Journal.Open(path, ct);
        Assert.Equal(15, Owed(reopened.WorkResources(Root, ct)));
        var method = Assert.Single(reopened.ActivityMethods(Root, ct));
        Assert.Equal(second.Id, method.Plan);
        Assert.Equal("eat-later", Assert.Single(method.Batch.Tasks).Id);
        Assert.Throws<ConflictException>(() => reopened.ContinueActivityMethod(first.Id, Root, new(300), Owners(batch, "completed"),
            Plan("replay", Assert.Single(reopened.ActivityMethods(Root, ct)).Batch), ct));
        Assert.Equal(15, Owed(reopened.WorkResources(Root, ct)));
    }
}
