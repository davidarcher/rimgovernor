using RimGovernor.Brain.Executor;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;
using Journal = RimGovernor.Brain.Store.Store;
using NativeReceipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class UndraftJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static readonly PlanSpec Release = new(new("release"), new(1),
        [DomainAction.NewUndraftAction(new("release-pawn"), new(new("pawn")))]);

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }

    private sealed class Native : IBoundary, IMovementBoundary, IReviewSource, IReviewWorkBenchSource
    {
        public Action? Inspect;
        public int Writes;
        public bool LoseReply;
        public ReviewFrame Frame = new();
        public Task<ReviewFrame> ReadReviewFrameAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => Task.FromResult(Frame);
        public Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> TickAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<Tables> FrameTablesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<DefinitionCatalog> DefinitionCatalogAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct)
        {
            Inspect?.Invoke();
            return Task.FromResult(new IntentInspection { StartedAt = DateTimeOffset.UtcNow,
                ObservedAt = DateTimeOffset.UtcNow, Current = target.Snapshot, Tick = new(100) });
        }

        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes++;
            if (LoseReply) return Task.FromException<IReadOnlyList<NativeReceipt>>(new IOException("lost undraft reply"));
            return Task.FromResult<IReadOnlyList<NativeReceipt>>(placements.Select(p => new NativeReceipt
                { Action = p.Action.Id, Attempt = p.Attempt, Snapshot = p.Snapshot, Kind = Receipt.Accepted }).ToArray());
        }

        public Task<NativeReceipt> WriteMovementAsync(Placement placement, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Rig : IDisposable
    {
        public readonly Journal Journal;
        public readonly Native Native = new();
        public readonly Hands Hands;
        public Rig(bool admitRelease = true)
        {
            IntentActionRules.RegisterIntentKinds();
            Journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-undraft-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
            Journal.Review(new() { Current = Root, Tick = new(100), Enabled = true,
                Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, Ct);
            if (admitRelease)
                Assert.Equal(ClaimOutcome.Admitted, Journal.CommitClaimedMethod(MilitaryFamily.RestoreWorkers,
                    Root, new("restore-workers"), "", Release, Ct).Outcome);
            Hands = RimGovernor.Brain.Executor.Executor.NewWithMovement(new ExecutorJournal(Journal), Native, Native, new Clock(),
                new Limits { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10),
                    JournalTimeout = TimeSpan.FromSeconds(10) }, new PlanAuthorizer(Journal, true));
            Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = true });
        }

        public async Task<BatchItem> Run() => Assert.Single(await Hands.RunBatchAsync(Release.Id, [Release.Actions[0].Id], Ct));
        public ProgressView Progress => Assert.Single(Journal.LoadPlan(Release.Id, Ct).Progress).View;
        public void Dispose() { Hands.Dispose(); Journal.Dispose(); }
    }

    private sealed class Session : IPlayerSession, IWorldSource
    {
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(Root);
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => scope == Root;
        public bool TargetsWorld(World world) => true;
        public void Disable() { }
        public ControlState State() => new(Root, true, true);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<World> ReadWorldAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RestoreWorkersSweepAdmitsAReleaseThatHandsDispatches()
    {
        using var rig = new Rig(admitRelease: false);
        var pawn = PawnFixtures.Row("pawn") with { Drafted = true };
        var context = new RimGovernor.Brain.GameFacts.Common.ObservationContext
        {
            Identity = Boundary.Identity(Root), Tick = 100,
            NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(Root.Native.Value)
        };
        rig.Native.Frame = new()
        {
            Context = context,
            Pawns = new() { Context = context, Pawns = [pawn] },
            Emergency = new(context, new() { Colonists = [new() { Observed = pawn }] })
        };
        var session = new Session();
        using var player = Player.Create(new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), rig.Journal, session, session, Ct);
        var reviewer = new ColonyReview(player, rig.Native, new Clock(), Rules.DefaultPlannerSettings(), TimeSpan.FromSeconds(30));
        using (var entry = await player.EnterAsync("restore-workers-test", false, Ct))
            Assert.Equal(Verdict.BuildingReasonAdmitted, await reviewer.SweepDraftsAsync(new StepArbiter(), entry.Call, entry.Epoch));
        Assert.Equal(0, rig.Native.Writes);
        var method = Assert.Single(rig.Journal.LoadNeed(MilitaryFamily.RestoreWorkers, Root, Ct).Methods);
        var plan = rig.Journal.LoadPlan(method.Plan, Ct).Spec;
        Assert.Equal(new PawnId("pawn"), Assert.Single(plan.Actions).Undraft!.Pawn);
        Assert.Equal(plan.Actions[0], StoreJson.Deserialize<DomainAction>(StoreJson.Serialize(plan.Actions[0])));
        Assert.Null(Assert.Single(await rig.Hands.RunBatchAsync(plan.Id, [plan.Actions[0].Id], Ct)).Error);
        Assert.Equal(Stage.Completed, Assert.Single(rig.Journal.LoadPlan(plan.Id, Ct).Progress).View.Stage);
        Assert.Equal(1, rig.Native.Writes);
    }

    [Fact]
    public void DraftReleasePolicyKeepsNeededAndCustodyPawnsDrafted()
    {
        var idle = PawnFixtures.Row("idle") with { Drafted = true };
        var needed = PawnFixtures.Row("needed") with { Drafted = true };
        var capturer = PawnFixtures.Row("capturer") with { Drafted = true,
            Job = PawnFixtures.Row("capturer").Job with { DefName = "Capture" } };
        Assert.Equal([new PawnId("idle")], DraftReleaseRules.UndraftCandidates([needed, idle, capturer, idle], [new("needed")], "Arrest", "Capture"));
        Assert.True(DraftReleaseRules.ReleaseHeld(new() { Unseen = [new("missing")] }));
        Assert.True(DraftReleaseRules.ReleaseHeld(new()));
    }

    [Fact]
    public void ReleasingADraftClaimsTheSettingWithoutBlockingThePawnsOrdinaryWork()
    {
        var action = Release.Actions[0];
        var claims = MethodClaimsRules.MethodClaimsOf(new(),
            [new WorkAction(Release.Id, action, new ProgressView { Action = action.Id, Stage = Stage.Pending })]);
        Assert.Empty(claims.Pawns);
        Assert.Equal(["draft/pawn"], claims.Targets);
    }

    [Fact]
    public async Task ReleaseUsesTheDurableActionAndNormalHandsReceiptPath()
    {
        using var rig = new Rig();
        var action = Assert.Single(rig.Journal.LoadPlan(Release.Id, Ct).Spec.Actions);
        var intent = IntentActionRules.IntentAction("key", action).Intent!.Draft!;
        Assert.Equal("pawn", intent.PawnId);
        Assert.False(intent.Drafted);
        Assert.Null((await rig.Run()).Error);
        Assert.Equal(Stage.Completed, rig.Progress.Stage);
        Assert.True(rig.Progress.Received(Receipt.Accepted));
        Assert.Equal(1UL, rig.Progress.Attempt.Value);
        Assert.Equal(1, rig.Native.Writes);
    }

    [Fact]
    public async Task ANewDraftRequirementBeforeDispatchVetoesTheRelease()
    {
        using var rig = new Rig();
        rig.Native.Inspect = () => rig.Journal.CreatePlan(new(new("new-fight"), new(1),
            [DomainAction.NewOwnedDraftAction(new("draft-pawn"), new(new("pawn")))]), Ct);
        Assert.Contains("draft is required", Assert.IsType<ConflictException>((await rig.Run()).Error).Message);
        Assert.Equal(0, rig.Native.Writes);
        Assert.Equal(0UL, rig.Progress.Attempt.Value);
    }

    [Fact]
    public async Task RevokedAuthorityAfterInspectionCannotUndraft()
    {
        using var rig = new Rig();
        rig.Native.Inspect = () => rig.Hands.UpdateAuthority(new() { Snapshot = Root, Enabled = false });
        Assert.NotNull((await rig.Run()).Error);
        Assert.Equal(0, rig.Native.Writes);
        Assert.Equal(Stage.Pending, rig.Progress.Stage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchKeepsTheCurrentFightRosterDraftedWithoutHoldingUnrelatedPawns(bool onRoster)
    {
        using var rig = new Rig();
        rig.Native.Inspect = () =>
        {
            var fight = new PlanSpec(new("fight"), new(1), [DomainAction.NewAutoHomeAreaAction(new("home"), false)]);
            rig.Journal.CreatePlan(fight, Ct);
            rig.Journal.OpenCombatFight(fight.Id, new CombatMemory(), new World(Root.Colony, Root.Load, Root.Map),
                [new PawnId(onRoster ? "pawn" : "other")], Ct);
        };
        var result = await rig.Run();
        if (onRoster)
        {
            Assert.IsType<ConflictException>(result.Error);
            Assert.Equal(0, rig.Native.Writes);
        }
        else
        {
            Assert.Null(result.Error);
            Assert.Equal(1, rig.Native.Writes);
        }
    }

    [Fact]
    public async Task LostReleaseReceiptIsJournaledAndAnExplicitRetryRecordsANewAttempt()
    {
        using var rig = new Rig();
        rig.Native.LoseReply = true;
        Assert.IsType<IOException>((await rig.Run()).Error);
        Assert.True(rig.Progress.Received(Receipt.Unconfirmed));
        Assert.Equal(1UL, rig.Progress.Attempt.Value);
        rig.Native.LoseReply = false;
        Assert.Null((await rig.Run()).Error);
        Assert.Equal(2, rig.Native.Writes);
        Assert.Equal(2UL, rig.Progress.Attempt.Value);
        Assert.Equal(Stage.Completed, rig.Progress.Stage);
    }
}
