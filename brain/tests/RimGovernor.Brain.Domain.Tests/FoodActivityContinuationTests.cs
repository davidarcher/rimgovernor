using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodActivityContinuationTests
{
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static FoodActivityOption Option(string target = "crop", int count = 8, double travel = 20) =>
        new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", count, 30) { ApproachTicks = Known(travel), DeliveryTicks = Known(0.0), HarvestAllowed = Known(true) };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new() { Options = EquatableArray.From(options) };
    private static PawnActivities Harvest() => ActivityContinuationFixtures.Harvest();
    private static Observed Terminal(PawnActivities batch, string state, params FoodActivityOption[] options) => new(Read(options),
        new("method/pawn", "method", batch.Orders[0].Revision, 0, state, "", null, 110),
        new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 110 });
    private sealed record Observed(FoodActivityRead Food, RimGovernor.Contracts.Wire.Operations.PawnActivityExecution Execution, RimGovernor.Contracts.Wire.Common.ObservationContext Context);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalMethodJournalAndHandsIntentPreserveHarvestThenObservedDelivery(bool cancelled)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-food-continuation-" + Guid.NewGuid().ToString("N") + ".db");
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(path, ct);
        journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var harvest = Harvest();
        var plan = new PlanSpec(new("harvest"), new(1), [DomainAction.NewPawnActivitiesAction(new("harvest-action"), harvest)]);
        TestNeed.Commit(journal, Root, new("method"), plan, ct);
        TestNeed.File(journal, met: false, new("method"), ct);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        Assert.Equal(harvest.Tasks, stored.PawnActivities!.Tasks);
        Assert.Equal(PawnActivityKind.Work, IntentActionRules.IntentAction(stored.Id.Value, stored).Intent!.PawnActivities!.Batch!.Orders[0].Steps[0].Kind);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, stored.Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, stored.Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, stored.Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var read = Terminal(harvest, "completed", Option("observed-food", 4));
        var next = new PlanSpec(new("delivery"), new(1), [DomainAction.NewPawnActivitiesAction(new("deliver-action"), CoordinatedActivitiesRules.ContinueCoordinatedActivity(harvest, Known(true), TaskRead(harvest, read))!)]);
        journal.ContinueActivityMethod(plan.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], next, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = next.Id }, ct);
        var intent = IntentActionRules.IntentAction(next.Actions[0].Id.Value, journal.LoadPlan(next.Id, ct).Spec.Actions[0]).Intent!.PawnActivities!.Batch!;
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
        var release = CoordinatedActivitiesRules.ContinueCoordinatedActivity(obligation.Batch, obligation.Needed, TaskRead(obligation.Batch, read))!;
        var cleanup = new PlanSpec(new("cleanup"), new(1), [DomainAction.NewPawnActivitiesAction(new("cleanup-action"), release)]);
        journal.ContinueActivityMethod(next.Id, Root, new(110), [new("pawn", "method/pawn", "method", 1, "completed")], cleanup, ct);
        journal.AuthorizeRoundsPlan(Root, Root with { Plan = cleanup.Id }, ct);
        Assert.True(journal.LoadPlan(cleanup.Id, ct).Spec.Actions[0].PawnActivities!.Orders[0].Release);
    }

    private static ActivityTaskRead TaskRead(PawnActivities batch, Observed read) => new()
    {
        Context = read.Context, Known = true,
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn, read.Execution))),
        Bindings = EquatableArray.From(batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Deliver)
            .SelectMany(t => read.Food.Options.Where(o => o.Pawn == t.Pawn && o.Resource == t.Resource)
                .Select(o => new ActivityTaskBinding(t.Id, t.Step with { Target = o.Target, Count = Math.Min(t.Step.Count, o.Count) })))),
    };
    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(Root.Colony, Root.Load, Root.Map));
    }
    private sealed class Reads(Observed read) : IArrivalPawns, IActivityTaskSource
    {
        public Task<ActivityTaskRead> ReadActivityTasksAsync(ActivityTaskRequest request, CancellationToken ct) => Task.FromResult(
            TaskRead(request.Batch, read) with
            {
                Bindings = EquatableArray.From(request.Batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Deliver)
                    .SelectMany(task => read.Food.Options.Where(o => o.Pawn == task.Pawn && o.Resource == task.Resource)
                        .Select(observed => new ActivityTaskBinding(task.Id, task.Step with
                            { Target = observed.Target, Count = Math.Min(task.Step.Count, observed.Count) })))),
            });
        public Task<ConstructionActivityRead> ReadConstructionActivityAsync(ConstructionActivityRequest request, CancellationToken ct) => Task.FromResult(new ConstructionActivityRead());
        public Task<PawnSnapshot> ReadPawnsAsync(RimGovernor.Contracts.Wire.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult(new PawnSnapshot { Context = read.Context, Pawns = [] });
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
            Sent.TrySetResult(IntentActionRules.IntentAction(action.Id.Value, action).Intent!.PawnActivities!.Batch!);
            return Task.FromResult<IReadOnlyList<RimGovernor.Brain.Executor.BatchItem>>([]);
        }
    }

    [Fact]
    public async Task WorkerObservesHarvestAndDispatchesDeliveryThroughItsNormalSession()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ct = lifetime.Token;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-food-worker-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        IntentActionRules.RegisterIntentKinds();
        journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var batch = Harvest();
        var plan = new PlanSpec(new("harvest"), new(1), [DomainAction.NewPawnActivitiesAction(new("harvest-action"), batch)]);
        TestNeed.Commit(journal, Root, new("method"), plan, ct);
        TestNeed.File(journal, met: false, new("method"), ct);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var session = new DispatchSession(journal);
        using var player = Player.Create(new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), journal, session, new Worlds(), ct);
        var terminal = Terminal(batch, "completed", Option("live-observed-food", 2));
        var read = terminal with { Context = new() { Identity = Boundary.Identity(Root), NativeGeneration = 1, Tick = 200 } };
        var worker = Worker.CreateInternal(new() { RoundsMethods = true, Pawns = new Reads(read),
            StepInterval = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1), StepTimeout = TimeSpan.FromSeconds(5) }, player, session, ct);
        try
        {
            var delivered = await session.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal("live-observed-food", delivered.Orders[0].Steps[0].Target);
            Assert.Equal(PawnActivityKind.Deliver, delivered.Orders[0].Steps[0].Kind);
            Assert.Equal(2, delivered.Orders[0].Steps[0].Count);
            Assert.Equal(500, delivered.Orders[0].Steps[0].Deadline);
            Assert.True(journal.LoadPlan(plan.Id, ct).Retired);
        }
        finally { await worker.Close(CancellationToken.None); }
    }

    [Fact]
    public async Task AFinishedCoupledTaskWaitsForTheReviewBeforeItsWorkerStartsTheNext()
    {
        // #3624: the worker's next task waits for the review its finished task raised: the step is woken as a quiet review with
        // the clock running, and the Method binds nothing for that worker until the review has run.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ct = lifetime.Token;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-task-review-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        IntentActionRules.RegisterIntentKinds();
        journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var step = new PawnActivityStep(PawnActivityKind.Work, "crop", "Harvest", "Harvest", 0, 2000);
        var first = new SelectedActivityTask("harvest", "pawn", [], step, new(2, 1), 0);
        var cook = new SelectedActivityTask("cook", "pawn", [first.Id], new(PawnActivityKind.Recipe, "bench", "Cook", "", 0, 2000)
            { Recipe = new("bill/CookRice", "CookRice", [new("raw", 10)]) }, new(2, 1), 0);
        var order = new PawnActivityOrder("pawn", "coupled-cook/pawn", "coupled-cook", 0, 1, false, [step]) { Task = first.Id };
        var batch = new PawnActivities(1, [order]) { Tasks = [first, cook] };
        var plan = new PlanSpec(new("cook"), new(1), [DomainAction.NewPawnActivitiesAction(new("cook-action"), batch)]);
        TestNeed.Commit(journal, Root, new("coupled-cook"), plan, ct);
        TestNeed.File(journal, met: false, new("coupled-cook"), ct);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, Receipt.Accepted, ct);
        var session = new DispatchSession(journal);
        using var player = Player.Create(new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), journal, session, new Worlds(), ct);
        var read = new Observed(Read(), new(order.Assignment, order.Method, 1, 0, "completed", "", null, 200),
            new() { Identity = Boundary.Identity(Root), NativeGeneration = 1, Tick = 200 });
        var wake = new WakeSignal();
        var worker = Worker.CreateInternal(new() { RoundsMethods = true, Pawns = new Reads(read), Wake = wake,
            StepInterval = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1), StepTimeout = TimeSpan.FromSeconds(5) }, player, session, ct);
        try
        {
            await WakeSignal.WaitStep(wake, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(StepCause.Review, wake.TakeInvalidated().Cause);
            Assert.True(player.TaskReviews.Waiting("coupled-cook/pawn/1"));
            var held = Assert.Single(journal.ActivityMethods(Root, ct)).Batch;
            Assert.Equal((1L, "harvest"), (Assert.Single(held.Orders).Revision, held.Orders[0].Task));
            Assert.Equal(batch.Tasks, held.Tasks);
            Assert.False(session.Sent.Task.IsCompleted);
            // The review ran: the worker goes on with the Method's next transition for that worker.
            player.TaskReviews.Reviewed(player.TaskReviews.Pending()!.Value);
            Assert.False(player.TaskReviews.Waiting("coupled-cook/pawn/1"));
            wake.NotifyPlanner("review");
            var next = await session.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal((1L, 2L), (Assert.Single(next.Orders).ExpectedRevision, next.Orders[0].Revision));
        }
        finally { await worker.Close(CancellationToken.None); }
    }
}
