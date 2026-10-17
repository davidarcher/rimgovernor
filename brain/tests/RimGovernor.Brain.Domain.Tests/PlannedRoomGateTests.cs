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
/// The room reconcile's portfolio gate (#3278): an unstarted planned room the latest comparison assessed is held unless
/// it selected some of the room's sites, which are placed exactly, in their stuff; a started room or one never assessed
/// is placed as before. The record is the Rounder's in-memory RoomSelection, set here as the review would set it.
/// </summary>
public sealed class PlannedRoomGateTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId Bedroom = NeedId.Of("test-rooms", "room", "a");

    /// <summary>Room 1: a 3x3 interior at (5,5), its ring from (4,4) to (8,8), the door south at (6,4).</summary>
    private static readonly PlannedRoom Room = new()
    {
        Id = 1, Role = PlannedRole.Bedroom, Region = RectRegion(new Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }),
        Door = new Cell(6, 4), DoorRot = Rotation.South,
    };

    private static readonly LayoutPlan Plan = new() { Rooms = [Room], NextRoomId = 2 };

    public interface ITestNative : IRoundsSource, IRoundsWorkBenchSource, IRoundsBuildingSource;

#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Native : DispatchProxy
    {
        /// <summary>The cells a blueprint of ours stands on: the preview refuses them, naming the blueprint, as the game does.</summary>
        public HashSet<Cell> Blueprints { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IRoundsBuildingSource.PreviewBuildingAsync))
            {
                throw new NotSupportedException(targetMethod?.Name + " is outside the building rig");
            }
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var building = action.Building!;
            var own = Blueprints.Contains(building.Cell);
            var preview = new BuildingPreview
            {
                Preview = new Preview
                {
                    Action = action, Snapshot = snapshot, Tick = new(100), CanPlace = Known(!own), SafeToPlace = Known(true),
                    Blockers = own ? [new PlacementBlocker { DefName = "Blueprint_" + building.Definition, Blueprint = true }] : [],
                },
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

    private sealed class RoomsFamily : IFamily<bool>
    {
        public string Id => "test-rooms";

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

        public IReadOnlyList<Need> Derive(bool unused) => [new Need { Id = Bedroom, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet }];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, bool unused) => [];
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required Player Player { get; init; }
        public required RoundsBuildingPlanner Planner { get; init; }
        public required HashSet<Cell> Blueprints { get; init; }
        public required FamilyRunner<bool> Runner { get; init; }

        /// <summary>The construction census the reconcile reads: an empty colony census until a test places something.</summary>
        public Fact<CurrentConstruction> Census { get; set; } = Known(new CurrentConstruction { Colony = true });

        public void Dispose() => Journal.Dispose();

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

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == Bedroom).Verdict;

        /// <summary>The buildings the run's methods placed.</summary>
        public IReadOnlyList<Building> Placed(FamilyRun run) => [.. run.Methods
            .SelectMany(m => Journal.LoadPlan(m.Plan, TestContext.Current.CancellationToken).Progress)
            .Select(p => p.Action.Building).OfType<Building>()];
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-planned-gate-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a");
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 1000), new(new("BlocksGranite"), 1000)]),
            Colonists = Known(1L), IndoorCapacity = Known(1L), BedCapacity = Known(1L), FieldCoverage = Known(1.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new(ShellWallDefinition, "WoodLog", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 5)])) { Size = Known(new Cell(1, 1)) },
                    new(ShellDoorDefinition, "WoodLog", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 25)])) { Size = Known(new Cell(1, 1)) },
                    new(ShellWallDefinition, "BlocksGranite", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("BlocksGranite"), 5)])) { Size = Known(new Cell(1, 1)) },
                    new(ShellDoorDefinition, "BlocksGranite", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("BlocksGranite"), 25)])) { Size = Known(new Cell(1, 1)) }]),
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
        var projection = new ColonyProjection
        {
            Identity = new() { Tick = new(100) },
            Bounds = new(50, 50),
            LayoutPlan = Known(Plan),
            Rooms = Known(new RoomObservation()),
            Definitions =
            [
                new PlanningDefinition { Name = ShellWallDefinition, Available = Known(true) },
                new PlanningDefinition { Name = ShellDoorDefinition, Available = Known(true) },
            ],
        };
        Colony? colony = null;
        var runner = new FamilyRunner<bool>(new RoomsFamily(), (_, _) => Task.FromResult(false), null, async (work, _, call) =>
        {
            var review = await journal.LoadRoundsAsync(call);
            var read = new RoundsReading
            {
                Emergency = new(),
                Frame = new(),
                Reading = new ColonyReading { Projection = projection with { Facts = projection.Facts with { CurrentConstruction = colony!.Census } } },
            };
            // A department room step's ring call: the room's own stuff is wood until the selection says otherwise.
            var rr = new RoomReconcile { RingOnly = true, Room = Room, Name = "test-bedroom", Reason = "test room", Stuff = (_, _) => ("WoodLog", "WoodLog", default, true) };
            return (await planner.ReconcileRoomAsync(work.State, review, work.Owner, read, null, rr, call, work.Epoch)).Verdict;
        });
        colony = new Colony { Journal = journal, Player = player, Planner = planner, Runner = runner, Blueprints = ((Native)(object)native).Blueprints };
        return colony;
    }

    private static SelectedPlannedSite Site(OpKind op, string stuff, params Cell[] cells) =>
        new("planned/room1/" + (op == OpKind.WallIn ? "wall" : "door") + "/" + stuff, 1, op, op == OpKind.WallIn ? ShellWallDefinition : ShellDoorDefinition,
            stuff, Rotation.North, [.. cells]);

    private static void Select(Colony colony, params SelectedPlannedSite[] sites) =>
        colony.Planner.Reviewer.RoomSelection.Set(World, new PlannedSelection { Assessed = [1], Sites = [.. sites], Tick = 100 });

    [Fact]
    public async Task AnUnassessedRoomIsPlacedAsBefore()
    {
        using var colony = Open();
        var run = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        var placed = colony.Placed(run);
        Assert.Equal(16, placed.Count);
        Assert.All(placed, b => Assert.Equal("WoodLog", b.Stuff));
    }

    [Fact]
    public async Task AnAssessedUnstartedRoomTheComparisonDidNotSelectPlacesNothing()
    {
        using var colony = Open();
        Select(colony);
        var run = await colony.RunAsync();
        Assert.Empty(run.Methods);
        Assert.True(Colony.Report(run).Is(Cause.AwaitingPlan), Colony.Report(run).ToString());
    }

    [Fact]
    public async Task ASelectedRoomPlacesOnlyItsSelectedSitesInTheirStuffThenContinuesAsPlacedConstruction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        // The comparison selected the south wall run in granite, not the door.
        var south = new[] { new Cell(4, 4), new Cell(5, 4), new Cell(7, 4), new Cell(8, 4) };
        Select(colony, Site(OpKind.WallIn, "BlocksGranite", south));
        var first = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(first).Outcome);
        var placed = colony.Placed(first);
        Assert.Equal(south.ToHashSet(), placed.Select(b => b.Cell).ToHashSet());
        Assert.All(placed, b => Assert.Equal((ShellWallDefinition, "BlocksGranite"), (b.Definition, b.Stuff)));

        // Its blueprints stand on the room's own ring: the room is started, so the rest is placed as before, whatever the record says.
        colony.Census = Known(new CurrentConstruction { Colony = true, Sites = [.. placed.Select(b => new ConstructionSite { Building = b, Stage = "blueprint" })] });
        colony.Blueprints.UnionWith(south);
        Select(colony);
        var next = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(next).Outcome);
        var rest = colony.Placed(next);
        Assert.Contains(rest, b => b.Definition == ShellDoorDefinition && b.Cell == Room.Door);
        Assert.DoesNotContain(rest, b => south.Contains(b.Cell));
        Assert.All(rest, b => Assert.Equal("WoodLog", b.Stuff));
        Assert.Equal(2, colony.Journal.OwnerMethodHistory(colony.Journal.LoadNeed(Bedroom, World, ct), ct).Count);
    }

    [Fact]
    public async Task AStartedRoomKeepsPlacingRegardlessOfSelection()
    {
        using var colony = Open();
        // A colony-built wall on the room's own ring starts it.
        colony.Census = Known(new CurrentConstruction
        {
            Colony = true,
            Buildings = [new CurrentBuilding { Id = "w", Building = new Building(ShellWallDefinition, new(4, 8), Rotation.North, "WoodLog"), Cells = [new(4, 8)] }],
        });
        Select(colony);
        var run = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal(15, colony.Placed(run).Count);
    }
}
