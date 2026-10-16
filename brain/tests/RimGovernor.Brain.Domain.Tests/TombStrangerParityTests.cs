using System.Runtime.CompilerServices;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The #3217 Gate 1 parity benchmark: today's stranger-sarcophagus decisions in Tomb.cs (burial, stack cap,
/// fresh-sarcophagus-then-dispose, decline when unfunded, unknown mood facts) written as scenarios, facts in and
/// decision out, through the narrowest public entries. The Gate 1 cutover (#3225) retargets these same
/// scenarios at the card-driven path and must keep them passing.
/// </summary>
public sealed class TombStrangerParityTests
{
    private const string Sarcophagus = "Sarcophagus";

    private static readonly PieceShapes Shapes = new()
    {
        Defs = new Dictionary<string, InteriorPieceDef> { [Sarcophagus] = new(Sarcophagus, new Cell(1, 2), RoomRole.None, null) },
        Furniture = new RoomFurniture { Sarcophagus = Sarcophagus },
    };

    private static readonly PlannedRoom TombRoom = new()
    {
        Id = 1,
        Role = PlannedRole.Tomb,
        Region = RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 4 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static readonly LayoutPlan Plan = new() { Rooms = [TombRoom], NextRoomId = 2 };

    private static WasteItem Corpse(string id, CorpseOf of, bool everBuried = false) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Exposed, Eligible = true, Cell = new Cell(30, 30), CorpseOf = of, EverBuriedInSarcophagus = everBuried };

    private static WasteItem Buried(string id, CorpseOf of, string grave) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Buried, CorpseOf = of, Grave = grave };

    private static CurrentBuilding Standing(string id, int x, int z) => new()
    {
        Id = id,
        Building = new Building(Sarcophagus, new Cell(x, z), Rotation.North, "WoodLog"),
        Cells = [new Cell(x, z), new Cell(x, z + 1)],
    };

    private static MoodPawn Colonist(string id, params double[] knowBuried) => new()
    {
        Id = new PawnId(id),
        Thoughts = Known<EquatableArray<MoodThought>>([.. knowBuried.Select(o => new MoodThought(KnowBuriedInSarcophagusThought, o)), new MoodThought("AteWithoutTable", -3)]),
    };

    private static TombStep Step(IReadOnlyList<WasteItem> waste, StrangerTomb strangers, IReadOnlyList<CurrentBuilding>? built = null, bool sarcophagus = true) =>
        NextTombStep(Plan, waste, built ?? [], Shapes, sarcophagus, strangers);

    private static readonly StrangerTomb Open = new() { Live = 0, Funded = true };

    // Burial: a stranger corpse is staged into a free tomb-room sarcophagus slot while it has an allowance.

    [Fact]
    public void AStrangerCorpseWithAnAllowanceIsStagedIntoAFreeTombSlot()
    {
        var step = Step([Corpse("raider", CorpseOf.Stranger)], Open);
        Assert.Equal(TombStepKind.Reconcile, step.Kind);
        Assert.Equal(TombRoom.Id, step.Room.Id);
        Assert.Equal(Sarcophagus, Assert.Single(step.Template).DefName);
        Assert.Equal(1, step.Dead);
        var owed = TombOwed(Shapes, Known(true), Known(Plan), Known(new RoomObservation()),
            Known<EquatableArray<WasteItem>>([Corpse("raider", CorpseOf.Stranger)]), Known(new CurrentConstruction { Colony = true }), Open);
        Assert.True(owed.TryGetValue(out var o) && o);
    }

    [Fact]
    public void AStrangerEverBuriedInASarcophagusIsSkipped()
    {
        Assert.Equal(TombStepKind.None, Step([Corpse("raider", CorpseOf.Stranger, everBuried: true)], Open).Kind);
        var owed = TombOwed(Shapes, Known(true), Known(Plan), Known(new RoomObservation()),
            Known<EquatableArray<WasteItem>>([Corpse("raider", CorpseOf.Stranger, everBuried: true)]), Known(new CurrentConstruction { Colony = true }), Open);
        Assert.True(owed.TryGetValue(out var o) && !o);
    }

    // Stack cap: the live stacks come from summed offsets and the colonists' maximum; Allowed is max(4 - Live, 0).

    [Theory]
    [InlineData(4.0, 1)]
    [InlineData(6.0, 2)]
    [InlineData(7.0, 3)]
    [InlineData(7.5, 4)]
    [InlineData(5.0, 1)]
    public void LiveStacksAreReadFromTheSummedOffset(double total, int live)
    {
        var (n, ok) = KnowBuriedStacks(Known<EquatableArray<MoodPawn>>([Colonist("a", total)]));
        Assert.True(ok);
        Assert.Equal(live, n);
    }

    [Fact]
    public void LiveStacksAreTheMostAnyColonistCarries()
    {
        var (n, ok) = KnowBuriedStacks(Known<EquatableArray<MoodPawn>>([Colonist("a", 4), Colonist("b", 7), Colonist("c")]));
        Assert.True(ok);
        Assert.Equal(3, n);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(6, 0)]
    public void AllowedIsTheCapLessTheLiveStacks(int live, int allowed) =>
        Assert.Equal(allowed, new StrangerTomb { Live = live, Funded = true }.Allowed());

    [Fact]
    public void AFullStackAdmitsNoStranger()
    {
        Assert.Equal(TombStepKind.None, Step([Corpse("raider", CorpseOf.Stranger)], new StrangerTomb { Live = StrangerTombStackCap, Funded = true }).Kind);
        Assert.Equal(TombStepKind.Reconcile, Step([Corpse("raider", CorpseOf.Stranger)], new StrangerTomb { Live = StrangerTombStackCap - 1, Funded = true }).Kind);
    }

    [Fact]
    public void StrangersBeyondTheAllowanceAreNotCountedDead()
    {
        var waste = new[] { Corpse("a", CorpseOf.Stranger), Corpse("b", CorpseOf.Stranger), Corpse("c", CorpseOf.Stranger) };
        Assert.Equal(1, Step(waste, new StrangerTomb { Live = 3, Funded = true }).Dead);
        Assert.Equal(3, Step(waste, Open).Dead);
    }

    // Fresh-then-dispose: a planned-tomb sarcophagus holding only strangers is deconstructed while the memory is live.

    [Fact]
    public void AFilledStrangerSarcophagusIsDisposedBeforeAnyStaging()
    {
        var built = new[] { Standing("s1", 10, 11) };
        var waste = new[] { Buried("raider", CorpseOf.Stranger, "s1"), Corpse("next", CorpseOf.Stranger) };
        var live = new StrangerTomb { Live = 1, Funded = true };
        var disposal = Assert.Single(StrangerDisposals(Plan, waste, built, Sarcophagus, live));
        Assert.Equal("s1", disposal.Id);
        var step = Step(waste, live, built);
        Assert.Equal(TombStepKind.Dispose, step.Kind);
        Assert.Equal("s1", Assert.Single(step.Disposal).Id);
        Assert.Empty(step.Template);
    }

    [Fact]
    public void NoDisposalWithoutALiveMemory()
    {
        var built = new[] { Standing("s1", 10, 11) };
        var waste = new[] { Buried("raider", CorpseOf.Stranger, "s1") };
        Assert.Empty(StrangerDisposals(Plan, waste, built, Sarcophagus, new StrangerTomb { Live = 0, Funded = true }));
        Assert.NotEqual(TombStepKind.Dispose, Step(waste, Open, built).Kind);
    }

    [Fact]
    public void ASarcophagusHoldingAColonistOrOutsideThePlannedTombIsNeverDisposed()
    {
        var live = new StrangerTomb { Live = 1, Funded = true };
        var mixed = new[] { Buried("raider", CorpseOf.Stranger, "s1"), Buried("ann", CorpseOf.Colonist, "s1") };
        Assert.Empty(StrangerDisposals(Plan, mixed, [Standing("s1", 10, 11)], Sarcophagus, live));
        var outside = new[] { Buried("raider", CorpseOf.Stranger, "s2") };
        Assert.Empty(StrangerDisposals(Plan, outside, [Standing("s2", 40, 40)], Sarcophagus, live));
    }

    // Decline when unfunded: no funding, no allowance; ReviewStrangers leaves Funded unset once the stack is full.

    [Fact]
    public void AnUnfundedSarcophagusGivesNoAllowance()
    {
        var unfunded = new StrangerTomb { Live = 0, Funded = false };
        Assert.Equal(0, unfunded.Allowed());
        Assert.Equal(TombStepKind.None, Step([Corpse("raider", CorpseOf.Stranger)], unfunded).Kind);
        Assert.Equal(4, Open.Allowed());
        Assert.Equal(TombStepKind.Reconcile, Step([Corpse("raider", CorpseOf.Stranger)], Open).Kind);
    }

    [Fact]
    public void ReviewStrangersLeavesFundedUnsetOnceTheStackIsFull()
    {
        var full = Review(Known<EquatableArray<MoodPawn>>([Colonist("a", 7.5)]));
        Assert.Equal(new StrangerTomb { Live = StrangerTombStackCap, Funded = false }, full);
        Assert.Equal(0, full.Allowed());
    }

    // Unknowns: unread mood facts give Ok == false and no stranger allowance.

    [Fact]
    public void UnknownMoodFactsGiveNoStrangerAllowance()
    {
        Assert.False(KnowBuriedStacks(Unknown<EquatableArray<MoodPawn>>()).Ok);
        var unread = new MoodPawn { Id = new PawnId("b"), Thoughts = Unknown<EquatableArray<MoodThought>>() };
        Assert.False(KnowBuriedStacks(Known<EquatableArray<MoodPawn>>([Colonist("a", 4), unread])).Ok);
        var strangers = Review(Unknown<EquatableArray<MoodPawn>>());
        Assert.Equal(new StrangerTomb(), strangers);
        Assert.Equal(0, strangers.Allowed());
        Assert.Equal(TombStepKind.None, Step([Corpse("raider", CorpseOf.Stranger)], strangers).Kind);
    }

    [Fact]
    public void KnownMoodFactsWithNoMemoryAreOk()
    {
        var (live, ok) = KnowBuriedStacks(Known<EquatableArray<MoodPawn>>([Colonist("a")]));
        Assert.True(ok);
        Assert.Equal(0, live);
    }

    /// <summary>
    /// Runs Rounder.ReviewStrangers over the mood census. The unknown and over-cap paths read no reviewer state, so
    /// an uninitialized reviewer stands in for the native-backed one; the funded path needs the resource targets
    /// and is not reached here.
    /// </summary>
    private static StrangerTomb Review(Fact<EquatableArray<MoodPawn>> pawns)
    {
        var rounder = (Rounder)RuntimeHelpers.GetUninitializedObject(typeof(Rounder));
        var projection = new ColonyProjection { Facts = new RoundsFacts { MoodPawns = pawns } };
        return rounder.ReviewStrangers(default, projection, CancellationToken.None).Strangers;
    }
}
