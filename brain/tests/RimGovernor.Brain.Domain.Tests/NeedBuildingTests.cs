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
/// release, and an overlapping ring under another Need is held by the claim. The food-stores/refrigeration Need's
/// cooler (#3280) is admitted beside a ring room under another Need when their claims do not overlap. A cooler,
/// a laboratory bench or a ring whose building is gone from the census is admitted again as the next attempt
/// (decision 19, #3301); open work, a standing or unfinished building, or an unread census holds it.
/// </summary>
public sealed class NeedBuildingTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId RoomA = NeedId.Of("test-rooms", "room", "a");
    private static readonly NeedId RoomB = NeedId.Of("test-rooms", "room", "b");

    /// <summary>A ring room beside the cooler, filed in the cooler's family (a family owns its NeedIds).</summary>
    private static readonly NeedId ColdRoom = NeedId.Of(FoodStoresFamilyId, "room", "a");

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
            return Task.FromResult(preview);
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

    /// <summary>The refrigeration Need's cooler beside room a's ring: both unmet, neither ordered after the other.</summary>
    private sealed class CoolerFamily : IFamily<bool>
    {
        public string Id => FoodStoresFamilyId;

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

        public IReadOnlyList<Need> Derive(bool overlap) =>
        [
            new Need { Id = ColdRoom, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet },
            new Need { Id = RefrigerationNeed, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet },
        ];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, bool overlap) => [];
    }

    /// <summary>The economy family's research Need alone, unmet: its work step raises the laboratory bench.</summary>
    private sealed class BenchFamily : IFamily<bool>
    {
        public string Id => EconomyFamily.FamilyId;

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

        public IReadOnlyList<Need> Derive(bool overlap) => [new Need { Id = EconomyFamily.Research, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet }];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, bool overlap) => [];
    }

    private sealed class Colony : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();

        public required Journal Journal { get; init; }
        public required Player Player { get; init; }
        public required FamilyRunner<bool> Runner { get; init; }
        public bool Overlap { get; set; }

        /// <summary>The construction census the work step reads; unknown until a test observes one.</summary>
        public Fact<CurrentConstruction> Census { get; set; }

        /// <summary>Applies every action of <paramref name="plan"/>: each building is placed (its blueprint accepted).</summary>
        public void Apply(PlanId plan)
        {
            var ct = TestContext.Current.CancellationToken;
            var scope = World with { Plan = plan, Revision = new(1) };
            foreach (var progress in Journal.LoadPlan(plan, ct).Progress)
            {
                var id = progress.Action.Id;
                _ = Journal.Prepare(plan, id, scope, new(90), ct);
                var dispatched = Journal.Dispatch(plan, id, scope, new(90), ct);
                _ = Journal.RecordReceipt(plan, id, dispatched.View.Attempt, Receipt.Accepted, ct);
                if (!progress.Action.Kind.IntentMode())
                {
                    _ = Journal.Observe(plan, new RimGovernor.Brain.Domain.Observation(id, dispatched.View.Attempt, scope, new(91), Effect.Completed), scope, ct);
                }
            }
        }

        /// <summary>A colony census in which every building of <paramref name="plans"/> stands built.</summary>
        public Fact<CurrentConstruction> Standing(params PlanId[] plans)
        {
            var ct = TestContext.Current.CancellationToken;
            var built = plans.SelectMany(p => Journal.LoadPlan(p, ct).Progress).Where(p => p.Action.Building is not null)
                .Select((p, i) => new CurrentBuilding { Id = "b" + i, Building = p.Action.Building });
            return Known(new CurrentConstruction { Colony = true, Buildings = [.. built] });
        }

        /// <summary>A colony census in which nothing stands.</summary>
        public static Fact<CurrentConstruction> Empty => Known(new CurrentConstruction { Colony = true });

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

    private static Colony Open(IFamily<bool>? family = null)
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
                    new(ShellDoorDefinition, "WoodLog", Known<EquatableArray<Amount>>([new(new("WoodLog"), 25)])) { Size = Known(new Cell(1, 1)) },
                    new("Cooler", "", Known<EquatableArray<Amount>>([new(new("WoodLog"), 10)])) { Size = Known(new Cell(1, 1)) },
                    new("SimpleResearchBench", "WoodLog", Known<EquatableArray<Amount>>([new(new("WoodLog"), 75)])) { Size = Known(new Cell(1, 1)) }]),
            },
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var timeout = TimeSpan.FromSeconds(10);
        var player = Player.Create(new PlayerConfig(timeout, timeout), journal, new Session(), new Worlds(), ct);
        var native = DispatchProxy.Create<ITestNative, Native>();
        var planner = new RoundsBuildingPlanner
        {
            Reviewer = new Rounder(player, native, new Clock(), RoundsRules.DefaultRoundsPolicy(), TimeSpan.FromSeconds(30)),
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
        var runner = new FamilyRunner<bool>(family ?? new RoomsFamily(), (_, _) => Task.FromResult(colony!.Overlap), null, async (work, _, call) =>
        {
            var review = await journal.LoadRoundsAsync(call);
            var read = reading with { Reading = reading.Reading with { Projection = reading.Reading.Projection with { Facts = reading.Reading.Projection.Facts with { CurrentConstruction = colony!.Census } } } };
            if (work.Need.Id == RefrigerationNeed || work.Need.Id == EconomyFamily.Research)
            {
                // The cooler step's and the bench step's admission (RefrigerationStepAsync, the building step): one
                // building, a Cooler on a wall cell well clear of room a's ring or the laboratory bench, behind the
                // planner's once-check over the read's census.
                var cooler = work.Need.Id == RefrigerationNeed;
                var method = new MethodId(cooler ? "refrigeration-cooler-20-20" : "laboratory-SimpleResearchBench");
                if (!planner.MethodOnce(work.Owner, method, call, read.Reading.Projection.Facts.CurrentConstruction))
                {
                    return Verdict.WaitFor(Cause.MethodUsed, "sleeping_method");
                }
                var snapshot = World with { Plan = PlanIds.MintPlanId(), Revision = new(1) };
                var building = cooler ? new Building("Cooler", new(20, 20), Rotation.North, "") : new Building("SimpleResearchBench", new(30, 30), Rotation.North, "WoodLog");
                var action = DomainAction.NewBuildingAction(new ActionId(snapshot.Plan.Value + "-0"), building, PlannerTier(work.Need.Id.FilingKey, Phase.None));
                var preview = new Preview { Action = action, Snapshot = snapshot, Tick = new(100), CanPlace = Known(true), SafeToPlace = Known(true) };
                var admitted = await planner.AdmitPreviewsAsync(new RoundsAdmission
                {
                    State = work.State,
                    Review = review,
                    Owner = work.Owner,
                    Facts = read.Reading.Projection,
                    Method = method,
                    Snapshot = snapshot,
                    Selected = [preview],
                    Stock = new StockObservation { Snapshot = snapshot, Tick = new(100) },
                    Purpose = Purpose.Rounds,
                }, call, work.Epoch);
                return admitted.Verdict;
            }
            var a = work.Need.Id == RoomA || work.Need.Id == ColdRoom;
            var room = Ring(a ? "test-room-a" : "test-room-b", a ? 4 : 1, roof: a);
            var built = await planner.CommitBuildsAsync(work.State, review, work.Owner, read, new LayoutPlan(), room, call, work.Epoch);
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
        // A cancelled wave built nothing, so nothing of it is gone: an empty census does not re-issue it.
        colony.Census = Colony.Empty;
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

    [Fact]
    public async Task TheRefrigerationCoolerAndARingRoomAreAdmittedSideBySide()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open(new CoolerFamily());

        var run = await colony.RunAsync();
        var cooler = Colony.Report(run, RefrigerationNeed);
        Assert.Equal(Outcome.Admitted, cooler.Outcome);
        Assert.Equal(Outcome.Admitted, Colony.Report(run, ColdRoom).Outcome);
        var coolerMethod = Assert.Single(run.Methods, m => m.Need == RefrigerationNeed);
        Assert.Equal(RefrigerationNeed.FilingKey, colony.Journal.PlanMethod(coolerMethod.Plan, ct)!.Concern);
        Assert.Contains(run.Methods, m => m.Need == ColdRoom);

        // Both stay open together: neither Need's open work holds the other's.
        Assert.True(WorkProgress.Open(colony.Journal.LoadPlan(coolerMethod.Plan, ct).Progress));
        Assert.All(run.Methods.Where(m => m.Need == ColdRoom), m => Assert.True(WorkProgress.Open(colony.Journal.LoadPlan(m.Plan, ct).Progress)));
        Assert.Single(colony.Journal.LoadNeed(RefrigerationNeed, World, ct).Methods);
    }

    /// <summary>
    /// Decision 19 (#3301): a Need's MethodId is held while its attempt is open or what it built stands, and is
    /// admitted again as the next attempt once the family's census shows the building gone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADestroyedCoolerOrBenchIsAdmittedAgainAsTheNextAttempt(bool cooler)
    {
        var ct = TestContext.Current.CancellationToken;
        var need = cooler ? RefrigerationNeed : EconomyFamily.Research;
        using var colony = Open(cooler ? new CoolerFamily() : new BenchFamily());
        var first = await colony.RunAsync();
        Assert.True(Colony.Report(first, need).Outcome == Outcome.Admitted, Colony.Report(first, need).ToString());
        var placed = Assert.Single(first.Methods, m => m.Need == need);

        // The attempt is still open: it continues, and an empty census does not re-issue it.
        colony.Census = Colony.Empty;
        var open = await colony.RunAsync();
        Assert.DoesNotContain(open.Methods, m => m.Need == need);
        Assert.Equal(placed.Method, Assert.Single(open.Reports, r => r.Need == need).Method);

        colony.Apply(placed.Plan);
        colony.Census = colony.Standing(placed.Plan);
        var standing = await colony.RunAsync();
        Assert.DoesNotContain(standing.Methods, m => m.Need == need);
        Assert.True(Colony.Report(standing, need).Is(Cause.MethodUsed));

        // An unread census never counts the building gone.
        colony.Census = default;
        Assert.DoesNotContain((await colony.RunAsync()).Methods, m => m.Need == need);

        colony.Census = Colony.Empty;
        var rebuilt = await colony.RunAsync();
        Assert.True(Colony.Report(rebuilt, need).Outcome == Outcome.Admitted, Colony.Report(rebuilt, need).ToString());
        var again = Assert.Single(rebuilt.Methods, m => m.Need == need);
        Assert.Equal((placed.Method, ClaimOutcome.Admitted), (again.Method, again.Outcome));
        Assert.NotEqual(placed.Plan, again.Plan);
        Assert.Equal(2, Assert.Single(colony.Journal.LoadNeed(need, World, ct).Methods, m => m.Plan == again.Plan).Attempt);

        // The new attempt is open work again: it continues.
        Assert.DoesNotContain((await colony.RunAsync()).Methods, m => m.Need == need);
    }

    [Fact]
    public void APlanProductIsGoneOnlyWhenNoAppliedBuildingIsBuiltOrASite()
    {
        var wall = new Building(ShellWallDefinition, new(1, 1), Rotation.North, "WoodLog");
        var door = new Building(ShellDoorDefinition, new(2, 1), Rotation.North, "WoodLog");
        var spec = new PlanSpec(new("p"), new(1), [DomainAction.NewBuildingAction(new("p-0"), wall, ConstructionTier.Survive), DomainAction.NewBuildingAction(new("p-1"), door, ConstructionTier.Survive)]);
        var scope = World with { Plan = spec.Id, Revision = spec.Revision };
        Progress Applied(ActionId id)
        {
            var p = new Progress(spec, id).Prepare(scope, new(90)).MarkDispatched(scope, new(90));
            p = p.RecordReceipt(p.View.Attempt, Receipt.Accepted);
            return p.Action.Kind.IntentMode() ? p : p.Observe(new RimGovernor.Brain.Domain.Observation(id, p.View.Attempt, scope, new(91), Effect.Completed), scope);
        }
        IReadOnlyList<Progress> applied = [Applied(new("p-0")), Applied(new("p-1"))];

        Assert.True(PlanProductGone(applied, Colony.Empty));
        Assert.False(PlanProductGone(applied, default));
        Assert.False(PlanProductGone(applied, Known(new CurrentConstruction())));
        // One piece standing, built or as a site, keeps the product standing.
        Assert.False(PlanProductGone(applied, Known(new CurrentConstruction { Colony = true, Buildings = [new CurrentBuilding { Id = "w", Building = wall }] })));
        Assert.False(PlanProductGone(applied, Known(new CurrentConstruction { Colony = true, Sites = [new ConstructionSite { Building = door }] })));
        // A plan that applied nothing built nothing to lose.
        Assert.False(PlanProductGone([new Progress(spec, new("p-0")).Cancel(), new Progress(spec, new("p-1"))], Colony.Empty));
    }

    [Fact]
    public async Task ADestroyedRingIsCommittedAgainButAnUnfinishedOrStandingOneIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var first = await colony.RunAsync();
        var ring = Assert.Single(first.Methods, m => m.Method.Value.StartsWith("test-room-a-build-", StringComparison.Ordinal));
        colony.Apply(ring.Plan);

        // Its blueprints and frames still stand: the wave is unfinished, not gone.
        var sites = colony.Journal.LoadPlan(ring.Plan, ct).Progress.Select(p => new ConstructionSite { Building = p.Action.Building, Stage = "blueprint" });
        colony.Census = Known(new CurrentConstruction { Colony = true, Sites = [.. sites] });
        Assert.DoesNotContain((await colony.RunAsync()).Methods, m => m.Method == ring.Method);

        colony.Census = colony.Standing(ring.Plan);
        var standing = await colony.RunAsync();
        Assert.DoesNotContain(standing.Methods, m => m.Method == ring.Method);
        Assert.True(Colony.Report(standing, RoomA).Is(Cause.MethodUsed));

        colony.Census = Colony.Empty;
        var rebuilt = await colony.RunAsync();
        // The roof's attempt is still open; only the ring is committed again.
        Assert.True(Colony.Report(rebuilt, RoomA).Outcome == Outcome.Admitted, Colony.Report(rebuilt, RoomA).ToString());
        var again = Assert.Single(rebuilt.Methods);
        Assert.Equal((ring.Method, ClaimOutcome.Admitted), (again.Method, again.Outcome));
        Assert.Equal(2, Assert.Single(colony.Journal.LoadNeed(RoomA, World, ct).Methods, m => m.Plan == again.Plan).Attempt);
    }
}
