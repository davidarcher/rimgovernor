using System.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The room reconcile's portfolio gate (#3278, #3754): an unstarted ringed planned room is held unless the latest
/// comparison selected some of the room's sites, which are placed exactly, in their stuff; one no comparison assessed is
/// held too; a started room is placed as before. The record is the Rounder's in-memory RoomSelection, set here as the
/// review would set it. Each reconcile records whether the room is done (DoneRooms).
/// </summary>
public sealed class PlannedRoomGateTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId Bedroom = NeedId.Of("test-rooms", "room", "a");

    /// <summary>Room 1: a 3x3 interior at (5,5), its ring from (4,4) to (8,8), the door south at (6,4).</summary>
    private static readonly PlannedRoom Room = new()
    {
        Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }),
        Door = new Cell(6, 4), DoorRot = Rotation.South,
    };

    /// <summary>Room 2: the next owed bedroom, a 3x3 interior at (20,5), its door south at (21,4).</summary>
    private static readonly PlannedRoom Next = new()
    {
        Id = 2, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle { X = 20, Z = 5, Width = 3, Height = 3 }),
        Door = new Cell(21, 4), DoorRot = Rotation.South,
    };

    private static readonly LayoutPlan Plan = new() { Rooms = [Room, Next], NextRoomId = 3 };

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
                    Action = action, Snapshot = snapshot, CanPlace = Known(!own), SafeToPlace = Known(true),
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

        public IReadOnlyList<Need> DeriveSeriousHarm(bool facts) => [];

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

        /// <summary>The verdict the reconcile itself returned to its step, unset when it threw past the step.</summary>
        public Verdict? Reconciled { get; set; }

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

    /// <summary>The rig; with <paramref name="owed"/> the step walks those rooms as the bedroom step does (FirstUnheldRoomAsync).</summary>
    private static Colony Open(Func<RoundsFacts, RoundsFacts>? review = null, IReadOnlyList<PlannedRoom>? owed = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-planned-gate-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a");
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 1000), new(new("BlocksGranite"), 1000)]),
            Colonists = 1L, IndoorCapacity = 1L, BedCapacity = 1L, FieldCoverage = Known(1.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new(ContainmentCellRules.ShellWallDefinition, "WoodLog", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 5)])) { Size = Known(new Cell(1, 1)) },
                    new(ContainmentCellRules.ShellDoorDefinition, "WoodLog", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 25)])) { Size = Known(new Cell(1, 1)) },
                    new(ContainmentCellRules.ShellWallDefinition, "BlocksGranite", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("BlocksGranite"), 5)])) { Size = Known(new Cell(1, 1)) },
                    new(ContainmentCellRules.ShellDoorDefinition, "BlocksGranite", Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("BlocksGranite"), 25)])) { Size = Known(new Cell(1, 1)) }]),
            },
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = review is null ? facts : review(facts) }, ct);
        var timeout = TimeSpan.FromSeconds(10);
        var player = Player.Create(new PlayerConfig(timeout, timeout), journal, new Session(), new Worlds(), ct);
        var native = DispatchProxy.Create<ITestNative, Native>();
        var planner = new RoundsBuildingPlanner
        {
            Reviewer = new Rounder(player, native, new Clock(), RoundsRules.DefaultRoundsPolicy(), TimeSpan.FromSeconds(30)),
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
                new PlanningDefinition { Name = ContainmentCellRules.ShellWallDefinition, Available = Known(true) },
                new PlanningDefinition { Name = ContainmentCellRules.ShellDoorDefinition, Available = Known(true) },
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
            var verdict = owed is null
                ? (await planner.ReconcileRoomAsync(work.State, review, work.Owner, read, null, rr, call, work.Epoch)).Verdict
                : (await RoundsSleepingUpkeepPlanner.FirstUnheldRoomAsync(owed, room => planner.ReconcileRoomAsync(work.State, review, work.Owner, read, null,
                    rr with { Room = room, Name = "test-bedroom" + room.Id }, call, work.Epoch))).Verdict;
            colony.Reconciled = verdict;
            return verdict;
        });
        colony = new Colony { Journal = journal, Player = player, Planner = planner, Runner = runner, Blueprints = ((Native)(object)native).Blueprints };
        return colony;
    }

    private static SelectedPlannedSite Site(OpKind op, string stuff, params Cell[] cells) =>
        new(1, op, op == OpKind.WallIn ? ContainmentCellRules.ShellWallDefinition : ContainmentCellRules.ShellDoorDefinition,
            stuff, [.. cells]);

    private static void Select(Colony colony, params SelectedPlannedSite[] sites) =>
        colony.Planner.Reviewer.RoomSelection.Set(World, new PlannedSelection { Assessed = [1], Sites = [.. sites] });

    /// <summary>A colony-built wall on <paramref name="cell"/>: on a room's own ring it starts the room.</summary>
    private static CurrentBuilding Built(string def, Cell cell) =>
        new() { Id = def + cell.X + "_" + cell.Z, Building = new Building(def, cell, Rotation.North, "WoodLog"), Cells = [cell] };

    /// <summary>
    /// #3754: after a restart the record is empty, so no comparison has assessed the unstarted bedroom: its ring is held
    /// (portfolio_unassessed) and nothing is placed until the first comparison selects it.
    /// </summary>
    [Fact]
    public async Task ARestartPlacesNoNewRingUntilTheFirstComparison()
    {
        using var colony = Open();
        var held = await colony.RunAsync();
        Assert.Empty(held.Methods);
        var verdict = Assert.IsType<Verdict>(colony.Reconciled);
        Assert.True(verdict.PortfolioHolds, verdict.ToString());
        Assert.Equal(RoundsBuildingPlanner.PortfolioUnassessed, verdict.Refusal.Subject);

        // The first comparison selects the room's whole ring: it is placed.
        Select(colony, Site(OpKind.WallIn, "WoodLog", [.. Room.WallRing().Cells().Where(c => c != Room.Door)]), Site(OpKind.DoorIn, "WoodLog", Room.Door));
        var placed = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(placed).Outcome);
        Assert.Equal(16, colony.Placed(placed).Count);
    }

    /// <summary>
    /// #3754: a started room is open while its reconcile owes a wall, and done once the ring stands whole and nothing is
    /// owed: the next review's ring-closure projection reads it from DoneRooms.
    /// </summary>
    [Fact]
    public async Task AStartedRoomIsOpenWhileItOwesAWallAndDoneWhenItOwesNothing()
    {
        using var colony = Open();
        var ring = Room.WallRing().Cells().Where(c => c != Room.Door).ToList();
        colony.Census = Known(new CurrentConstruction { Colony = true, Buildings = [Built(ContainmentCellRules.ShellWallDefinition, ring[0])] });
        await colony.RunAsync();
        Assert.DoesNotContain(Room.Id, colony.Planner.Reviewer.DoneRooms.Get(World));

        colony.Census = Known(new CurrentConstruction
        {
            Colony = true,
            Buildings = [.. ring.Select(c => Built(ContainmentCellRules.ShellWallDefinition, c)), Built(ContainmentCellRules.ShellDoorDefinition, Room.Door)],
        });
        var done = await colony.RunAsync();
        Assert.Empty(done.Methods);
        Assert.Contains(Room.Id, colony.Planner.Reviewer.DoneRooms.Get(World));
    }

    /// <summary>
    /// #3754 (D3, option b): a started room whose diff owes only a bed blocked on ComplexFurniture is not open: no builder
    /// can work the bed until the research finishes, so the room frees them; once it has, the bed keeps the room open, and
    /// a wall still owed keeps it open regardless. A bed whose stuff the colony neither stocks nor finds on the map is
    /// blocked too.
    /// </summary>
    [Fact]
    public void ABedBlockedOnComplexFurnitureLeavesTheRoomDone()
    {
        var bed = new WantedPiece { DefName = "Bed", Minimum = new(5, 5), Maximum = new(5, 6), Size = new(1, 2), Rot = Rotation.North };
        IReadOnlyList<Operation> owed = [new Operation { Kind = OpKind.Build, Pieces = [bed] }];
        IReadOnlyList<Operation> walled = [new Operation { Kind = OpKind.WallIn, Cells = [new(4, 6)] }, .. owed];
        RoundsReading Reading(long wood, params string[] finished)
        {
            var projection = new ColonyProjection
            {
                Definitions =
                [
                    new PlanningDefinition
                    {
                        Name = "Bed", Available = Known(true), Research = ["ComplexFurniture"],
                        StuffOptions = [new StuffOption { Stuff = "WoodLog", Value = 1, Common = true, Costs = [new RimGovernor.Brain.Policy.Amount(new("WoodLog"), 45)] }],
                    },
                ],
                Resources = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [new("WoodLog")] = wood }),
                RingSources = Known(new RingStuffSources()),
            };
            projection = projection with
            {
                Facts = projection.Facts with { Research = Known(new ResearchFacts { Finished = [.. finished.Select(f => new ResearchProjectId(f))] }) },
            };
            return new RoundsReading { Emergency = new(), Frame = new(), Reading = new ColonyReading { Projection = projection } };
        }

        Assert.True(PlannedRules.PlannedRoomDone(owed, RoundsBuildingPlanner.PieceBlockedOf(Reading(100))));
        Assert.False(PlannedRules.PlannedRoomDone(walled, RoundsBuildingPlanner.PieceBlockedOf(Reading(100))));
        Assert.False(PlannedRules.PlannedRoomDone(owed, RoundsBuildingPlanner.PieceBlockedOf(Reading(100, "ComplexFurniture"))));
        Assert.True(PlannedRules.PlannedRoomDone(owed, RoundsBuildingPlanner.PieceBlockedOf(Reading(0, "ComplexFurniture"))));
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
        Assert.All(placed, b => Assert.Equal((ContainmentCellRules.ShellWallDefinition, "BlocksGranite"), (b.Definition, b.Stuff)));

        // Its blueprints stand on the room's own ring: the room is started, so the rest is placed as before, whatever the record says.
        colony.Census = Known(new CurrentConstruction { Colony = true, Sites = [.. placed.Select(b => new ConstructionSite { Building = b, Stage = "blueprint" })] });
        colony.Blueprints.UnionWith(south);
        Select(colony);
        var next = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(next).Outcome);
        var rest = colony.Placed(next);
        Assert.Contains(rest, b => b.Definition == ContainmentCellRules.ShellDoorDefinition && b.Cell == Room.Door);
        Assert.DoesNotContain(rest, b => south.Contains(b.Cell));
        Assert.All(rest, b => Assert.Equal("WoodLog", b.Stuff));
        Assert.Equal(2, colony.Journal.OwnerMethodHistory(colony.Journal.LoadNeed(Bedroom, World, ct), ct).Count);
    }

    [Fact]
    public async Task ABedroomStepFallsThroughARoomThePortfolioHoldsToTheNextOwedRoom()
    {
        // #3743: owed rooms [1, 2]; the comparison assessed room 1 and selected none of it, so it is held. The step
        // does not stop there: room 2, started by a wall on its own ring, is reconciled instead.
        using var colony = Open(owed: [Room, Next]);
        colony.Census = Known(new CurrentConstruction { Colony = true, Buildings = [Built(ContainmentCellRules.ShellWallDefinition, new(19, 8))] });
        Select(colony);
        var run = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        var placed = colony.Placed(run);
        Assert.Equal(15, placed.Count);
        var ring = Next.Region.Walls().Cells().ToHashSet();
        Assert.All(placed, b => Assert.Contains(b.Cell, ring));
    }

    [Fact]
    public async Task ABedroomStepWithEveryOwedRoomHeldReturnsTheFirstHold()
    {
        using var colony = Open(owed: [Room, Next]);
        colony.Planner.Reviewer.RoomSelection.Set(World, new PlannedSelection { Assessed = [1, 2], Sites = [] });
        var run = await colony.RunAsync();
        Assert.Empty(run.Methods);
        var verdict = Assert.IsType<Verdict>(colony.Reconciled);
        Assert.True(verdict.Is(Cause.AwaitingPlan), verdict.ToString());
        Assert.Contains("test-bedroom1", verdict.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartedRoomKeepsPlacingRegardlessOfSelection()
    {
        using var colony = Open();
        // A colony-built wall on the room's own ring starts it.
        colony.Census = Known(new CurrentConstruction
        {
            Colony = true,
            Buildings = [new CurrentBuilding { Id = "w", Building = new Building(ContainmentCellRules.ShellWallDefinition, new(4, 8), Rotation.North, "WoodLog"), Cells = [new(4, 8)] }],
        });
        Select(colony);
        var run = await colony.RunAsync();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal(15, colony.Placed(run).Count);
    }

    [Fact]
    public async Task ARingTheSharedAdmissionRefusesIsTheStepsVerdictNotAThrow()
    {
        // Shared admission refused the storage room's ring by throwing past the stockpile step, so the step never reached
        // its zone edits. A refusal is an outcome: the reconcile returns it, and the step decides what goes on.
        using var colony = Open(f => f with { WorkAdmission = f.WorkAdmission with { Buildings = Known<EquatableArray<WorkBuildingCost>>([]) } });
        colony.Census = Known(new CurrentConstruction { Colony = true, Buildings = [Built(ContainmentCellRules.ShellWallDefinition, new(4, 8))] });
        var run = await colony.RunAsync();
        Assert.Empty(run.Methods);
        var verdict = Assert.IsType<Verdict>(colony.Reconciled);
        Assert.True(verdict.Is(Cause.SharedAdmission), verdict.ToString());
        Assert.Contains("unknown_building_materials", verdict.ToString(), StringComparison.Ordinal);
    }
}
