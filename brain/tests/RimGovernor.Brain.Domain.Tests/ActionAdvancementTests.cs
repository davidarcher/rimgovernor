using System.Diagnostics;
using RimGovernor.Brain.Executor;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;
using Hands = RimGovernor.Brain.Executor.Executor;
using NativeReceipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ActionAdvancementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("a"), new(1), new(1));
    private static DomainAction Wall(string id, int x = 1) => DomainAction.NewBuildingAction(new(id),
        new("TestWall", new(x, 1), Rotation.North, "Rice"), ConstructionTier.Survive);

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }

    private sealed class Scope : IReviewScope
    {
        public Task AuthorizeReviewPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Native : IBoundary, IMovementBoundary
    {
        public readonly List<string[]> Writes = [];
        public Func<Target, CancellationToken, Task>? Inspect;
        public Exception? WriteError;
        public async Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct)
        {
            if (Inspect is { } inspect) await inspect(target, ct);
            return new() { StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow, Current = target.Snapshot, Tick = new(200) };
        }

        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes.Add(placements.Select(p => p.Action.Id.Value).ToArray());
            if (WriteError is { } error) return Task.FromException<IReadOnlyList<NativeReceipt>>(error);
            return Task.FromResult<IReadOnlyList<NativeReceipt>>(placements.Select(p => new NativeReceipt
            { Action = p.Action.Id, Attempt = p.Attempt, Snapshot = p.Snapshot, Kind = Receipt.Accepted }).ToArray());
        }

        public async Task<NativeReceipt> WriteMovementAsync(Placement placement, CancellationToken ct) =>
            (await WriteIntentsAsync([placement], ct))[0];
    }

    private sealed class Rig : IExecutionSession, IDisposable
    {
        public readonly Journal Journal;
        public readonly Native Native = new();
        public readonly Hands Hands;
        public readonly ActionAdvancement Advance;
        public ControlState Authority = new(Root, true, true);
        public int Observed;
        public int Calls;
        public bool CancelFirst;
        public Rig(PlanSpec plan, int maxDispatches = 0, IArrivalPawns? pawns = null,
            Func<GameFacts.Common.Identity, CancellationToken, Task<EmergencyObservation>>? breaks = null)
        {
            IntentActionRules.RegisterIntentKinds();
            Journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-advance-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
            Journal.CreatePlan(plan, Ct);
            Journal.Review(new ReviewRequest { Current = Root, Tick = new(100), Enabled = true,
                Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create() }, Ct);
            var scope = new Scope();
            Hands = RimGovernor.Brain.Executor.Executor.NewWithMovement(new ExecutorJournal(Journal), Native, Native, new Clock(), new Limits
            { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10) }, scope);
            Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = true });
            Advance = new(new ActionAdvancementConfig
            {
                Journal = Journal, ReviewScope = scope, Session = this,
                Telemetry = () => new TelemetryEmitter(NullTelemetrySink.Instance), RecordOutcome = (_, _, _) => { },
                StepInterval = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(8), MaxDispatches = maxDispatches,
                Pawns = pawns, BreakPawns = pawns, BreakSource = breaks,
            });
        }

        public ControlState State() => Authority;
        public Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct)
        {
            Observed++;
            Authority = new(requested, true, false);
            Hands.UpdateAuthority(new() { Snapshot = requested, Enabled = false });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
        {
            Calls++;
            if (CancelFirst && Calls == 1) return Task.FromException<IReadOnlyList<BatchItem>>(new OperationCanceledException());
            return Hands.RunBatchAsync(plan, actions, ct);
        }

        public Task<BatchItem> ReassertMovementAsync(PlanId plan, ActionId action, Tick observed, CancellationToken ct) =>
            Hands.ReassertMovementAsync(plan, action, observed, ct);

        public Task Step(int seconds = 0) => Advance.AdvanceAsync(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds), Authority,
            new(Root.Colony, Root.Load, Root.Map), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10), Stopwatch.GetTimestamp(),
            new StepStages(), () => Ct.ThrowIfCancellationRequested(), Ct);
        public void Dispose() { Hands.Dispose(); Journal.Dispose(); }
    }

    [Fact]
    public async Task IndependentActionsBatchAndDependentActionWaitsForPredecessor()
    {
        var first = DomainAction.NewAutoHomeAreaAction(new("first"), false);
        var second = Wall("second", 2); var dependent = Wall("dependent", 3);
        using var rig = new Rig(new(Root.Plan, Root.Revision, [first, second, dependent], [new(dependent.Id, first.Id, true)]));
        await Assert.ThrowsAsync<DependencyException>(() => rig.Step());
        Assert.Equal(["first", "second"], Assert.Single(rig.Native.Writes));
        await rig.Step(2);
        Assert.Equal(["dependent"], rig.Native.Writes[1]);
        Assert.All(rig.Journal.LoadPlan(Root.Plan, Ct).Progress, p => Assert.Equal(Stage.Completed, p.View.Stage));
    }

    [Fact]
    public async Task FairnessGivesAnotherPlanATurnWhenFirstPlanStaysHeld()
    {
        using var rig = new Rig(new(Root.Plan, Root.Revision, [Wall("first")]), maxDispatches: 1);
        rig.Journal.CreatePlan(new(new("b"), new(1), [Wall("second", 2)]), Ct);
        rig.Native.Inspect = (target, _) => target.Action.Id.Value == "first" ? Task.FromException(new HeldException()) : Task.CompletedTask;
        await Assert.ThrowsAsync<HeldException>(() => rig.Step());
        await rig.Step(2);
        Assert.Equal(["second"], Assert.Single(rig.Native.Writes));
    }

    [Fact]
    public async Task OwnCancellationBeforeDispatchRetriesTheWholeBatch()
    {
        using var rig = new Rig(new(Root.Plan, Root.Revision, [Wall("first"), Wall("second", 2)])) { CancelFirst = true };
        await rig.Step();
        Assert.Equal(2, rig.Calls);
        Assert.Equal(["first", "second"], Assert.Single(rig.Native.Writes));
    }

    [Fact]
    public async Task LostWriteReplyIsRecordedAndCannotResendUnderSameAuthority()
    {
        using var rig = new Rig(new(Root.Plan, Root.Revision, [Wall("first")])) ;
        rig.Native.WriteError = new IOException("lost reply");
        await Assert.ThrowsAsync<IOException>(() => rig.Step());
        var progress = Assert.Single(rig.Journal.LoadPlan(Root.Plan, Ct).Progress).View;
        Assert.True(progress.Received(Receipt.Unconfirmed));
        await rig.Step(2);
        Assert.Single(rig.Native.Writes);
        rig.Native.WriteError = null;
        rig.Authority = rig.Authority with { Snapshot = Root with { Native = new(2) } };
        rig.Hands.UpdateAuthority(new() { Snapshot = rig.Authority.Snapshot, Enabled = true });
        await rig.Step(4);
        Assert.Equal(2, rig.Native.Writes.Count);
        Assert.Equal(Stage.Completed, Assert.Single(rig.Journal.LoadPlan(Root.Plan, Ct).Progress).View.Stage);
    }

    [Fact]
    public async Task PausedOutstandingAttemptIsReconciledWithoutANativeWrite()
    {
        var action = Wall("first");
        using var rig = new Rig(new(Root.Plan, Root.Revision, [action]));
        rig.Journal.Prepare(Root.Plan, action.Id, Root, new(100), Ct);
        rig.Journal.Dispatch(Root.Plan, action.Id, Root, new(100), Ct);
        rig.Authority = rig.Authority with { Enabled = false };
        rig.Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = false });
        await rig.Step();
        Assert.Equal(1, rig.Observed);
        Assert.Empty(rig.Native.Writes);
        Assert.True(Assert.Single(rig.Journal.LoadPlan(Root.Plan, Ct).Progress).View.Received(Receipt.Unconfirmed));
    }

    [Fact]
    public async Task AuthorityChangeDuringInspectionPreventsTheWrite()
    {
        using var rig = new Rig(new(Root.Plan, Root.Revision, [Wall("first")]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Native.Inspect = async (_, _) => { entered.SetResult(); await release.Task.WaitAsync(Ct); };
        var step = rig.Step();
        await entered.Task.WaitAsync(Ct);
        rig.Authority = rig.Authority with { Enabled = false };
        rig.Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = false });
        release.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => step);
        Assert.Empty(rig.Native.Writes);
        Assert.Equal(0UL, Assert.Single(rig.Journal.LoadPlan(Root.Plan, Ct).Progress).View.Attempt.Value);
    }

    private sealed class Pawns : IArrivalPawns
    {
        public Action? Reading;
        public GameFacts.Common.ObservationContext Context = new()
        {
            Identity = ActionObservations.Identity(Root),
            NativeGeneration = new GameFacts.Common.ObservedGeneration.AtCase(1), Tick = 101,
        };
        public GameFacts.Observations.PawnState Row = EmergencyFixtures.Placed("mover") with { Drafted = true };
        public Task<GameFacts.Observations.PawnSnapshot> ReadPawnsAsync(GameFacts.Common.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct)
        {
            Reading?.Invoke();
            return Task.FromResult(new GameFacts.Observations.PawnSnapshot { Context = Context, Pawns = [Row] });
        }
    }

    private static PlanSpec MovementPlan()
    {
        var draft = DomainAction.NewOwnedDraftAction(new("draft"), new(new("mover")));
        var move = DomainAction.NewMovementAction(new("move"), new(new("mover"), new(5, 5), draft.Id));
        var wall = Wall("wall");
        return new(Root.Plan, Root.Revision, [draft, move, wall], [new(move.Id, draft.Id, true), new(wall.Id, move.Id, true)]);
    }

    private static void CompleteMovement(Rig rig)
    {
        foreach (var id in new[] { new ActionId("draft"), new ActionId("move") })
        {
            rig.Journal.Prepare(Root.Plan, id, Root, new(100), Ct);
            var sent = rig.Journal.Dispatch(Root.Plan, id, Root, new(100), Ct);
            rig.Journal.RecordReceipt(Root.Plan, id, sent.View.Attempt, Receipt.Accepted, Ct);
        }
    }

    [Fact]
    public async Task ArrivalReassertionRecordsANewAttemptAndReleasesOnlyAtTheDestination()
    {
        var pawns = new Pawns();
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        CompleteMovement(rig);
        await rig.Step();
        Assert.Equal(["move"], Assert.Single(rig.Native.Writes));
        var move = rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "move").View;
        Assert.Equal(2UL, move.Attempt.Value);
        Assert.Equal(Stage.Completed, move.Stage);
        pawns.Row = pawns.Row with { Pawn = pawns.Row.Pawn with { Place = new GameFacts.Observations.EntityPlace.OnMap(1, new Cell(5, 5)) } };
        await rig.Step(2);
        Assert.Equal(["wall"], rig.Native.Writes[1]);
    }

    [Fact]
    public async Task AuthorityRevokedDuringArrivalReadCannotReassertMovement()
    {
        var pawns = new Pawns();
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        CompleteMovement(rig);
        pawns.Reading = () =>
        {
            rig.Authority = rig.Authority with { Enabled = false };
            rig.Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = false });
        };
        await rig.Step();
        Assert.Empty(rig.Native.Writes);
        Assert.Equal(1UL, rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "move").View.Attempt.Value);
    }

    [Fact]
    public async Task StaleArrivalRowsCannotReleaseOrReassertWork()
    {
        var pawns = new Pawns();
        pawns.Context = pawns.Context with { NativeGeneration = new GameFacts.Common.ObservedGeneration.AtCase(2) };
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        CompleteMovement(rig);
        await rig.Step();
        Assert.Empty(rig.Native.Writes);
    }

    [Fact]
    public void CompletedMovementReprepareRejectsAnotherWorldOrOlderAuthority()
    {
        using var rig = new Rig(MovementPlan());
        CompleteMovement(rig);
        var completed = rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "move");
        Assert.Throws<DomainException>(() => completed.Prepare(Root with { Load = new("another-load") }, new(101)));
        Assert.Throws<DomainException>(() => completed.Prepare(Root with { Native = new(0) }, new(101)));
        var draft = rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "draft");
        Assert.Throws<DomainException>(() => draft.Prepare(Root, new(101)));
    }

    [Fact]
    public async Task LostArrivalResendReceiptIsJournaledAndNotBlindlyRepeated()
    {
        var pawns = new Pawns();
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        CompleteMovement(rig);
        rig.Native.WriteError = new IOException("lost movement receipt");
        await rig.Step();
        var move = rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "move").View;
        Assert.Equal(2UL, move.Attempt.Value);
        Assert.True(move.Received(Receipt.Unconfirmed));
        await rig.Step(2);
        Assert.Single(rig.Native.Writes);
    }

    [Fact]
    public async Task InterruptedPreparedMovementResumesWithoutReleasingWorkBeforeArrival()
    {
        var pawns = new Pawns();
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        CompleteMovement(rig);
        // A stopped call may have journaled the reassertion's Prepare but not dispatched it.
        rig.Journal.Prepare(Root.Plan, new("move"), Root, new(101), Ct);
        await rig.Step();
        Assert.Equal(["move"], Assert.Single(rig.Native.Writes));
        Assert.Equal(2UL, rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "move").View.Attempt.Value);
        Assert.Equal(Stage.Pending, rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "wall").View.Stage);
    }

    [Fact]
    public async Task InitialMoveAndDependentWorkCannotDispatchTogetherBeforeArrival()
    {
        var pawns = new Pawns();
        using var rig = new Rig(MovementPlan(), pawns: pawns);
        var sent = rig.Journal.Prepare(Root.Plan, new("draft"), Root, new(100), Ct);
        sent = rig.Journal.Dispatch(Root.Plan, new("draft"), Root, new(100), Ct);
        rig.Journal.RecordReceipt(Root.Plan, new("draft"), sent.View.Attempt, Receipt.Accepted, Ct);
        await rig.Step();
        Assert.Equal(["move"], Assert.Single(rig.Native.Writes));
        Assert.Equal(Stage.Pending, rig.Journal.LoadPlan(Root.Plan, Ct).Progress.Single(p => p.Action.Id.Value == "wall").View.Stage);
    }

    [Fact]
    public async Task AggressiveBreakHoldsNearbyWorkUntilTheFreshCensusClearsIt()
    {
        var pawns = new Pawns { Row = EmergencyFixtures.Placed("breaker", 1, 1) };
        var breaker = EmergencyFixtures.Colonist("breaker") with
        {
            Observed = pawns.Row with { MentalState = new GameFacts.Observations.PawnMentalState { DefName = "Berserk", IsAggro = true, Ticks = 10 } },
        };
        var broken = true;
        using var rig = new Rig(new(Root.Plan, Root.Revision, [Wall("wall")]), pawns: pawns,
            breaks: (_, _) => Task.FromResult(new EmergencyObservation(pawns.Context,
                new EmergencyFacts { Colonists = broken ? [breaker] : [] })));
        await rig.Step();
        Assert.Empty(rig.Native.Writes);
        broken = false;
        await rig.Step(2);
        Assert.Equal(["wall"], Assert.Single(rig.Native.Writes));
    }
}
