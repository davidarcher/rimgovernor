using System.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared building planner under a Need owner (#3279): a test family's work step builds a ring room and a roof
/// through <c>CommitBuildsAsync</c> and <c>CommitRoof</c>; each target is committed once and not re-issued after
/// release, and an overlapping ring under another Need is held by the claim.
/// </summary>
public sealed class NeedBuildingTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId RoomA = NeedId.Of("test-rooms", "room", "a");
    private static readonly NeedId RoomB = NeedId.Of("test-rooms", "room", "b");

    /// <summary>The native the planner previews against: every building placement is ready.</summary>
    public interface ITestNative : IRoundsSource, IRoundsWorkBenchSource, IRoundsBuildingSource;

#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Native : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IRoundsBuildingSource.PreviewBuildingAsync))
            {
                throw new NotSupportedException(targetMethod?.Name + " is outside the building rig");
            }
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var preview = new BuildingPreview
            {
                Preview = new Preview { Action = action, Snapshot = snapshot, Tick = new(100), CanPlace = Known(true), SafeToPlace = Known(true) },
                Stock = new StockObservation { Snapshot = snapshot, Tick = new(100) },
            };
            return Task.FromResult((preview, Result.Empty));
        }
    }
#pragma warning restore CA1852

    private sealed class Session : IPlayerSession
    {
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(World);
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => true;
        public bool TargetsWorld(RimGovernor.Brain.Store.World world) => true;
        public void Disable()
        {
        }
        public ControlState State() => new(World, true, true);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Worlds : IWorldSource
    {
        public Task<RimGovernor.Brain.Store.World> ReadWorldAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UnixEpoch;
    }

    /// <summary>Room a always; room b, whose ring shares room a's west wall, when <see cref="Overlap"/> is set.</summary>
    private sealed class RoomsFamily : IFamily<bool>
    {
        public string Id => "test-rooms";

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

        public IReadOnlyList<Need> Derive(bool overlap) =>
        [
            new Need { Id = RoomA, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet },
            .. overlap ? [new Need { Id = RoomB, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet }] : Array.Empty<Need>(),
        ];

        // Building work needs previews between choosing and committing: nothing is proposed purely.
        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, bool overlap) => [];
    }

    private sealed class Colony : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();

        public required Journal Journal { get; init; }
        public required Player Player { get; init; }
        public required FamilyRunner<bool> Runner { get; init; }
        public bool Overlap { get; set; }

        public void Dispose()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            Journal.Dispose();
        }

        public async Task<FamilyRun> RunAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            using var entry = await Player.EnterAsync("test", false, ct);
            var epoch = entry.Epoch;
            return await Runner.RunAsync(new FamilyStep
            {
                Journal = Journal,
                World = World,
                State = new ControlState(World, true, true),
                Epoch = epoch,
                Fence = () => Player.Current(ct, epoch),
            }, ct);
        }

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static RoomWork Ring(string name, int x, bool roof)
    {
        var walls = new List<Cell>();
        for (var dz = 0; dz < 4; dz++)
        {
            walls.Add(new(x, dz));
            walls.Add(new(x + 3, dz));
        }
        walls.AddRange([new(x + 1, 0), new(x + 2, 0), new(x + 1, 3)]);
        List<Operation> ops =
        [
            new() { Kind = OpKind.WallIn, Cells = walls },
            new() { Kind = OpKind.DoorIn, Cells = [new(x + 2, 3)] },
        ];
        if (roof)
        {
            ops.Add(new() { Kind = OpKind.RoofIn, Cells = [new(x + 1, 1), new(x + 2, 1), new(x + 1, 2), new(x + 2, 2)] });
        }
        return new RoomWork
        {
            Rr = new RoomReconcile
            {
                Room = new PlannedRoom { Id = 1, Role = PlannedRole.Storage },
                Name = name,
                Reason = "test room",
                RingOnly = true,
                Stuff = (_, _) => ("WoodLog", "WoodLog", default, true),
            },
            Ops = ops,
        };
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-need-building-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("WoodLog"), 1000)]),
            Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new(ShellWallDefinition, "WoodLog", Known<EquatableArray<Amount>>([new(new("WoodLog"), 5)])) { Size = Known(new Cell(1, 1)) },
                    new(ShellDoorDefinition, "WoodLog", Known<EquatableArray<Amount>>([new(new("WoodLog"), 25)])) { Size = Known(new Cell(1, 1)) }]),
            },
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var timeout = TimeSpan.FromSeconds(10);
        var player = Player.Create(new PlayerConfig(timeout, timeout), journal, new Session(), new Worlds(), ct);
        var native = DispatchProxy.Create<ITestNative, Native>();
        var planner = new RoundsBuildingPlanner
        {
            Reviewer = new Rounder(player, native, new Clock(), DefaultRoundsPolicy(), TimeSpan.FromSeconds(30)),
            Native = native,
        };
        var reading = new RoundsReading
        {
            Emergency = new(),
            Frame = new(),
            Reading = new ColonyReading
            {
                Projection = new ColonyProjection
                {
                    Identity = new() { Tick = new(100) },
                    Bounds = new(50, 50),
                    Definitions =
                    [
                        new PlanningDefinition { Name = ShellWallDefinition, Available = Known(true) },
                        new PlanningDefinition { Name = ShellDoorDefinition, Available = Known(true) },
                    ],
                },
            },
        };
        Colony? colony = null;
        var runner = new FamilyRunner<bool>(new RoomsFamily(), (_, _) => Task.FromResult(colony!.Overlap), null, async (work, _, call) =>
        {
            var review = await journal.LoadRoundsAsync(call);
            var a = work.Need.Id == RoomA;
            var room = Ring(a ? "test-room-a" : "test-room-b", a ? 4 : 1, roof: a);
            var built = await planner.CommitBuildsAsync(work.State, review, work.Owner, reading, new LayoutPlan(), room, call, work.Epoch);
            var (roofed, _) = planner.CommitRoof(work.State, work.Owner, room, call, work.Epoch);
            return roofed.Verdict.Outcome == Outcome.Admitted ? roofed.Verdict : built.Verdict;
        });
        colony = new Colony { Journal = journal, Player = player, Runner = runner };
        return colony;
    }

    [Fact]
    public async Task ARingAndARoofAreCommittedOnceUnderTheNeedAndNotReissuedAfterRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();

        var first = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(first, RoomA).Outcome);
        Assert.Equal(2, first.Methods.Count);
        Assert.All(first.Methods, m => Assert.Equal((RoomA, ClaimOutcome.Admitted), (m.Need, m.Outcome)));
        Assert.Contains(first.Methods, m => m.Method.Value.StartsWith("test-room-a-build-", StringComparison.Ordinal));
        Assert.Contains(first.Methods, m => m.Method.Value.StartsWith("test-room-a-roof-", StringComparison.Ordinal));
        foreach (var m in first.Methods)
        {
            Assert.Equal(RoomA.FilingKey, colony.Journal.PlanMethod(m.Plan, ct)!.Concern);
        }

        var again = await colony.RunAsync();
        Assert.Empty(again.Methods);
        Assert.True(Colony.Report(again, RoomA).Is(Cause.MethodUsed));

        foreach (var m in first.Methods)
        {
            foreach (var progress in colony.Journal.LoadPlan(m.Plan, ct).Progress)
            {
                colony.Journal.Cancel(m.Plan, progress.Action.Id, ct);
            }
        }
        var released = await colony.RunAsync();
        Assert.Empty(released.Methods);
        Assert.True(Colony.Report(released, RoomA).Is(Cause.MethodUsed));
        Assert.Equal(2, colony.Journal.OwnerMethodHistory(colony.Journal.LoadNeed(RoomA, World, ct), ct).Count);
    }

    [Fact]
    public async Task AnOverlappingRingUnderAnotherNeedIsHeldByTheClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(2, (await colony.RunAsync()).Methods.Count);

        colony.Overlap = true;
        var run = await colony.RunAsync();
        var held = Colony.Report(run, RoomB);
        Assert.True(held.Is(Cause.Claim), held.ToString());
        Assert.DoesNotContain(run.Methods, m => m.Need == RoomB);
        Assert.Empty(colony.Journal.LoadNeed(RoomB, World, ct).Methods);
    }
}
