using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodActivityContinuationTests
{
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static FoodActivityOption Option(string target = "crop", int count = 8, double travel = 20) =>
        new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", count, travel, 30) { NutritionPerUnit = Known(1.0), ApproachTicks = Known(travel), DeliveryTicks = Known(0.0), HarvestAllowed = Known(true) };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new()
    { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 110 }, Options = EquatableArray.From(options) };
    private static PawnActivities Harvest() => StartFoodActivity(Read(Option()), 1, "method", 500, out _)!;
    private static FoodActivityRead Terminal(PawnActivities batch, string state, params FoodActivityOption[] options) => Read(options) with
    { Execution = new("method/pawn", "method", batch.Orders[0].Revision, 0, state, "", null, 110) };

    [Fact]
    public void NativeRouteCostChangesChoiceAndUnknownOrLateOptionsDoNotAcquire()
    {
        var slow = Option("slow", travel: 200);
        var fast = Option("fast", travel: 10);
        Assert.Equal("fast", StartFoodActivity(Read(slow, fast), 1, "m", 500, out _)!.Orders[0].Steps[0].Target);
        Assert.Equal("slow", StartFoodActivity(Read(slow, fast with { TravelTicks = 300, ApproachTicks = Known(300.0) }), 1, "m", 500, out _)!.Orders[0].Steps[0].Target);
        Assert.Null(StartFoodActivity(Read(fast) with { Known = false }, 1, "m", 500, out _));
        Assert.Null(StartFoodActivity(Read(fast with { TravelTicks = double.NaN, ApproachTicks = default }), 1, "m", 500, out _));
        Assert.Null(StartFoodActivity(Read(slow), 1, "m", 120, out _));
    }

    [Fact]
    public void FoodMethodUsesSharedOutcomeSelectionAndReportsOmittedAlternatives()
    {
        var options = Enumerable.Range(0, 17).Select(i => Option("crop-" + i.ToString("D2"), travel: i == 16 ? 0 : 20)).ToArray();
        var result = StartFoodActivity(Read(options), 1, "method", 500, out var comparison)!;
        Assert.NotNull(comparison);
        Assert.Single(comparison.Selected);
        Assert.Contains("generation_truncated", comparison.Findings);
        Assert.False(comparison.SearchTruncated);
        Assert.False(comparison.NetBenefit.IsKnown); // Efficiency is not fabricated avoided hunger loss.
        Assert.Equal("crop-00", result.Orders[0].Steps[0].Target);
        Assert.Contains(comparison.Plan!.Allocations, a => a.Activity == comparison.Selected[0]);
        StartFoodActivity(Read(Option()) with { Truncated = true }, 1, "m", 500, out comparison);
        Assert.Contains("generation_truncated", comparison!.Findings);
    }

    [Fact]
    public void ComparisonRetainsQuantityValuationStableTiesAndPostponement()
    {
        var small = Option("a-small", count: 1, travel: 10);
        var large = Option("b-large", count: 8, travel: 30);
        Assert.Equal("b-large", StartFoodActivity(Read(small, large), 1, "m", 500, out _)!.Orders[0].Steps[0].Target);
        Assert.Equal("a-small", StartFoodActivity(Read(large with { Count = 1 }, small), 1, "m", 500, out _)!.Orders[0].Steps[0].Target);
        Assert.Equal("a-small", StartFoodActivity(Read(small with { Target = "z-tie" }, small), 1, "m", 500, out _)!.Orders[0].Steps[0].Target);
        Assert.Null(StartFoodActivity(Read(large with { WorkTicks = 500 }), 1, "m", 500, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalMethodJournalAndHandsIntentPreserveHarvestThenObservedDelivery(bool cancelled)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-food-continuation-" + Guid.NewGuid().ToString("N") + ".db");
        BridgeFunctions.RegisterIntentKinds();
        using var journal = Journal.Open(path, ct);
        var review = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var harvest = Harvest();
        var plan = new PlanSpec(new("harvest"), new(1), [DomainAction.NewPawnActivitiesAction(new("harvest-action"), harvest)]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        Assert.Equal(harvest.Tasks, stored.PawnActivities!.Tasks);
        Assert.Equal(PawnActivityKind.Work, BridgeFunctions.IntentAction(stored.Id.Value, stored).Intent!.PawnActivities!.Batch!.Orders[0].Steps[0].Kind);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, stored.Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, stored.Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, stored.Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var read = Terminal(harvest, "completed", Option("observed-food", 4));
        var next = new PlanSpec(new("delivery"), new(1), [DomainAction.NewPawnActivitiesAction(new("deliver-action"), ReconsiderObservedFood(harvest, Known(true), TaskRead(harvest, read), read, out _)!)]);
        journal.ContinueActivityMethod(plan.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], next, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = next.Id }, ct);
        var intent = BridgeFunctions.IntentAction(next.Actions[0].Id.Value, journal.LoadPlan(next.Id, ct).Spec.Actions[0]).Intent!.PawnActivities!.Batch!;
        Assert.Equal("observed-food", intent.Orders[0].Steps[0].Target);
        Assert.Equal(4, intent.Orders[0].Steps[0].Count);
        Assert.Equal("method", intent.Orders[0].Method);
        Assert.Equal(3, intent.Tasks.Single().AttemptsLeft);
        // A stack can change again between the observation and Apply. Refusal leaves the harvest
        // assignment installed; its retired predecessor must still authorize cleanup through Hands.
        var deliveryScope = Root with { Plan = next.Id };
        journal.Prepare(next.Id, next.Actions[0].Id, deliveryScope, new(110), ct);
        if (cancelled) journal.Cancel(next.Id, next.Actions[0].Id, ct);
        else
        {
            var sent = journal.Dispatch(next.Id, next.Actions[0].Id, deliveryScope, new(110), ct);
            journal.RecordReceipt(next.Id, next.Actions[0].Id, sent.View.Attempt, Receipt.Refused, ct);
        }
        var obligation = Assert.Single(journal.ActivityMethods(Root, ct));
        Assert.Equal(1, obligation.Batch.Orders[0].Revision);
        Assert.False(obligation.Needed.Value);
        var release = ContinueCoordinatedActivity(obligation.Batch, obligation.Needed, TaskRead(obligation.Batch, read))!;
        var cleanup = new PlanSpec(new("cleanup"), new(1), [DomainAction.NewPawnActivitiesAction(new("cleanup-action"), release)]);
        journal.ContinueActivityMethod(next.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], cleanup, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = cleanup.Id }, ct);
        Assert.True(journal.LoadPlan(cleanup.Id, ct).Spec.Actions[0].PawnActivities!.Orders[0].Release);
    }

    private static ActivityTaskRead TaskRead(PawnActivities batch, FoodActivityRead food) => new()
    {
        Context = food.Context, Known = food.Known, Food = food,
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn, food.Execution))),
    };
    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(Root.Colony, Root.Load, Root.Map));
    }
    private sealed class Reads(FoodActivityRead read) : IArrivalPawns, IActivityTaskSource
    {
        public Task<ActivityTaskRead> ReadActivityTasksAsync(ActivityTaskRequest request, CancellationToken ct) => Task.FromResult(TaskRead(request.Batch, read));
        public Task<RecipeActivityRead> ReadRecipeActivityAsync(RecipeActivityRequest request, CancellationToken ct) => Task.FromResult(new RecipeActivityRead());
        public Task<ConstructionActivityRead> ReadConstructionActivityAsync(ConstructionActivityRequest request, CancellationToken ct) => Task.FromResult(new ConstructionActivityRead());
        public Task<ListPawnsReply> ReadCombatPawnsAsync(RimGovernor.Contracts.Wire.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult(new ListPawnsReply { Outcome = new PawnSnapshot { Context = read.Context } });
    }
    private sealed class DispatchSession(Journal journal) : IWorkerSession
    {
        internal readonly TaskCompletionSource<PawnActivities> Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(requested);
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => true;
        public bool TargetsWorld(World world) => true;
        public void Disable() { }
        public ControlState State() => new(Root, true, true);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RimGovernor.Brain.Executor.BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
        {
            var action = journal.LoadPlan(plan, ct).Spec.Actions.Single(a => a.Id == actions[0]);
            Sent.TrySetResult(BridgeFunctions.IntentAction(action.Id.Value, action).Intent!.PawnActivities!.Batch!);
            return Task.FromResult<IReadOnlyList<RimGovernor.Brain.Executor.BatchItem>>([]);
        }
    }

    [Fact]
    public async Task WorkerObservesHarvestAndDispatchesDeliveryThroughItsNormalSession()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ct = lifetime.Token;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-food-worker-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        BridgeFunctions.RegisterIntentKinds();
        var review = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var batch = Harvest();
        var plan = new PlanSpec(new("harvest"), new(1), [DomainAction.NewPawnActivitiesAction(new("harvest-action"), batch)]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var session = new DispatchSession(journal);
        using var player = Player.Create(new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), journal, session, new Worlds(), ct);
        var read = Terminal(batch, "completed", Option("live-observed-food", 2)) with
        { Context = new() { Identity = Boundary.Identity(Root), NativeGeneration = 1, Tick = 110 } };
        var worker = Worker.CreateInternal(new() { RoundsMethods = true, Pawns = new Reads(read),
            StepInterval = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1), StepTimeout = TimeSpan.FromSeconds(5) }, player, session, ct);
        try
        {
            var delivered = await session.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal("live-observed-food", delivered.Orders[0].Steps[0].Target);
            Assert.Equal(PawnActivityKind.Deliver, delivered.Orders[0].Steps[0].Kind);
            Assert.True(journal.LoadPlan(plan.Id, ct).Retired);
        }
        finally { await worker.Close(CancellationToken.None); }
    }
}
