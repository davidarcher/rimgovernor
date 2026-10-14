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
        new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", count, travel, 30);
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new()
    { Known = true, Context = new() { Tick = 110 }, Options = EquatableArray.From(options) };
    private static PawnActivities Harvest() => StartFoodActivity(Read(Option()), 1, "method", 500)!;
    private static FoodActivityRead Terminal(PawnActivities batch, string state, params FoodActivityOption[] options) => Read(options) with
    { Execution = new("method/pawn", "method", batch.Orders[0].Revision, 0, state, "", null, 110) };

    [Fact]
    public void HarvestBindsOnlyFreshObservedFoodAndClampsToChangedQuantity()
    {
        var harvest = Harvest();
        Assert.Equal("crop", harvest.Orders[0].Steps[0].Target);
        Assert.Null(ContinueFoodActivity(harvest, Known(true), Read(Option("food"))));
        var delivery = ContinueFoodActivity(harvest, Known(true), Terminal(harvest, "completed", Option("new-stack-after-merge", 3)))!;
        var step = Assert.Single(Assert.Single(delivery.Orders).Steps);
        Assert.Equal(PawnActivityKind.Deliver, step.Kind);
        Assert.Equal("new-stack-after-merge", step.Target);
        Assert.Equal(3, step.Count);
        Assert.Equal(new Cell(8, 9), step.Destination);
        Assert.Equal(500, step.Deadline);
        Assert.Equal(2, delivery.Orders[0].OutputDelivery!.AttemptsLeft);
    }

    [Fact]
    public void InterruptedDeliveryRebindsSplitStackAndChangedDestinationWithoutRenewingDeadline()
    {
        var delivery = ContinueFoodActivity(Harvest(), Known(true), Terminal(Harvest(), "completed", Option("old-stack")))!;
        var retry = ContinueFoodActivity(delivery, Known(true), Terminal(delivery, "failed",
            Option("split-stack", 2) with { Destination = new(10, 11) }))!;
        Assert.Equal("split-stack", retry.Orders[0].Steps[0].Target);
        Assert.Equal(2, retry.Orders[0].Steps[0].Count);
        Assert.Equal(500, retry.Orders[0].Steps[0].Deadline);
        Assert.Equal(1, retry.Orders[0].OutputDelivery!.AttemptsLeft);
        Assert.True(ContinueFoodActivity(retry, Known(true), Terminal(retry, "failed"))!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(delivery, Known(true), Terminal(delivery, "failed", Option("duplicate")) with { Carrying = true })!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(delivery, Known(true), Terminal(delivery, "completed", Option("still-at-source")))!.Orders[0].Release);
    }

    [Fact]
    public void UnknownsNewOwnersAndRunningWorkCannotTriggerReplacement()
    {
        var batch = Harvest();
        var read = Terminal(batch, "completed", Option("food"));
        Assert.Null(ContinueFoodActivity(batch, default, read));
        Assert.Null(ContinueFoodActivity(batch, Known(true), read with { Known = false }));
        Assert.Null(ContinueFoodActivity(batch, Known(true), read with { Execution = read.Execution! with { Assignment = "other" } }));
        Assert.Null(ContinueFoodActivity(batch, Known(true), read with { Execution = read.Execution! with { State = "running" } }));
        Assert.True(ContinueFoodActivity(batch, Known(false), read)!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(batch, Known(true), read with { Execution = read.Execution! with { State = "failed" } })!.Orders[0].Release);
        Assert.True(ContinueFoodActivity(batch, Known(true), read with { Context = new() { Tick = 500 } })!.Orders[0].Release);
    }

    [Fact]
    public void NativeRouteCostChangesChoiceAndUnknownOrLateOptionsDoNotAcquire()
    {
        var slow = Option("slow", travel: 200);
        var fast = Option("fast", travel: 10);
        Assert.Equal("fast", StartFoodActivity(Read(slow, fast), 1, "m", 500)!.Orders[0].Steps[0].Target);
        Assert.Equal("slow", StartFoodActivity(Read(slow, fast with { TravelTicks = 300 }), 1, "m", 500)!.Orders[0].Steps[0].Target);
        Assert.Null(StartFoodActivity(Read(fast) with { Known = false }, 1, "m", 500));
        Assert.Null(StartFoodActivity(Read(fast with { TravelTicks = double.NaN }), 1, "m", 500));
        Assert.Null(StartFoodActivity(Read(slow), 1, "m", 120));
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
            Policy = DefaultRoundsPolicy(), Facts = new() { Tick = new(100), LedgerOwed = Known(true) } }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var harvest = Harvest();
        var plan = new PlanSpec(new("harvest"), new(1), [DomainAction.NewPawnActivitiesAction(new("harvest-action"), harvest)]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        Assert.Equal(harvest.Orders[0].OutputDelivery, stored.PawnActivities!.Orders[0].OutputDelivery);
        Assert.Equal(PawnActivityKind.Work, BridgeFunctions.IntentAction(stored.Id.Value, stored).Intent!.PawnActivities!.Batch!.Orders[0].Steps[0].Kind);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, stored.Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, stored.Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, stored.Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var read = Terminal(harvest, "completed", Option("observed-food", 4));
        var next = new PlanSpec(new("delivery"), new(1), [DomainAction.NewPawnActivitiesAction(new("deliver-action"), ContinueFoodActivity(harvest, Known(true), read)!)]);
        journal.ContinueActivityMethod(plan.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], next, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = next.Id }, ct);
        var intent = BridgeFunctions.IntentAction(next.Actions[0].Id.Value, journal.LoadPlan(next.Id, ct).Spec.Actions[0]).Intent!.PawnActivities!.Batch!;
        Assert.Equal("observed-food", intent.Orders[0].Steps[0].Target);
        Assert.Equal(4, intent.Orders[0].Steps[0].Count);
        Assert.Equal("method", intent.Orders[0].Method);
        Assert.Equal(2, intent.Orders[0].OutputDelivery!.AttemptsLeft);
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
        var release = ContinueFoodActivity(obligation.Batch, obligation.Needed, read)!;
        var cleanup = new PlanSpec(new("cleanup"), new(1), [DomainAction.NewPawnActivitiesAction(new("cleanup-action"), release)]);
        journal.ContinueActivityMethod(next.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], cleanup, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = cleanup.Id }, ct);
        Assert.True(journal.LoadPlan(cleanup.Id, ct).Spec.Actions[0].PawnActivities!.Orders[0].Release);
    }

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(Root.Colony, Root.Load, Root.Map));
    }
    private sealed class Reads(FoodActivityRead read) : IArrivalPawns, IFoodActivitySource
    {
        public Task<FoodActivityRead> ReadFoodActivityAsync(FoodActivityRequest request, CancellationToken ct) => Task.FromResult(read);
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
            Policy = DefaultRoundsPolicy(), Facts = new() { Tick = new(100), LedgerOwed = Known(true) } }, ct);
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
