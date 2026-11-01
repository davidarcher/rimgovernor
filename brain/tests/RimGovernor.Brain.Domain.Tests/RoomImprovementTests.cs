using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001: a decoration enters the comparison priced by its real effect on the occupants' mood, not by a tier
/// floor. A piece that leaves the room at the Impressiveness stage its owner already sleeps at gains nothing, whatever it
/// costs; one that reaches the next stage gains the owner's priced bedroom thought over the horizon, against its build
/// cost. An unread room stat, mood or material value leaves the piece unassessed, and it is not planned.
/// </summary>
public sealed class RoomImprovementTests
{
    internal static readonly PawnId A = new("a");
    private static readonly Resource Wood = new("WoodLog");
    private static readonly NeedCell IndoorCell = new(true, false, false, false, false);

    private static NeedThoughtStage Stage(int stage, double offset) =>
        new("SleptInBedroom", "slept_in_bedroom", stage, offset, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false));

    /// <summary>The owner of bed b, sleeping in it at the dull stage now; each stage up the slept_in_bedroom ladder adds 2.</summary>
    private static NeedsForecastPawn Owner() => NeedsFixtures.Pawn() with
    {
        Pawn = A.Value, Mood = NeedsFixtures.Mood with { UnclampedTarget = 0.4, BreakThreshold = 0.35 }, OwnedBed = "b", Sleep = SleepFixtures.InBed("b"),
        ThoughtStages = [Stage(0, -2), Stage(1, 0), Stage(2, 2), Stage(3, 4), Stage(4, 6)],
        Targets = [NeedsFixtures.Service("b", "rest", 0d, 0) with { Bed = NeedsFixtures.BedRoom("Bedroom", 1, false), Cell = IndoorCell }],
    };

    internal static RoomImprovementFacts Facts(double materialValue = 1) => new()
    {
        Gains = new()
        {
            Pawns = new Dictionary<string, NeedsForecastPawn> { [A.Value] = Owner() },
            Thoughts = new Dictionary<string, EquatableArray<MoodThought>> { [A.Value] = [] },
            Breaks = new() { Threshold = 0.35, Recovery = new MoodRecovery.Measured(.25), Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            WellbeingWeight = 0.1,
        },
        Levels = BedMergePriceTests.Levels,
        Values = new Dictionary<Resource, ConstructionMaterialValue> { [Wood] = new(MaterialValueBasis.Labor, materialValue) },
    };

    /// <summary>A 20-cell bedroom scoring <paramref name="impressiveness"/>: 29 is dull, one point under mediocre.</summary>
    internal static UpkeepRoom Room(double impressiveness) => new()
    {
        StatKeys = SleepingNativeFixtures.StatKeys,
        Native = SleepingNativeFixtures.Room("r", RoomRole.Bedroom, new RoomQuality(28, 0.5, 0, 300, impressiveness), 20),
        Beds = ["b"],
    };

    /// <summary>A dresser-sized piece of 10 wood: <paramref name="beauty"/> and <paramref name="wealth"/> are its stats.</summary>
    private static RoomPieceEffect Piece(double beauty, double wealth) => new(beauty, wealth, 2, 0, [new(Wood, 10)]);

    private static RoomImprovementValue Value(UpkeepRoom room, RoomPieceEffect? piece, RoomImprovementFacts? facts = null, PawnId[]? owners = null) =>
        RoomQualityRules.ImprovementValueOf(room, owners ?? [A], piece, facts ?? Facts());

    [Fact]
    public void APieceThatLeavesTheStageGainsNothingWhateverItCosts()
    {
        // A token piece leaves the dull room dull; a mediocre room already meets what a small piece adds.
        Assert.Equal(new RoomImprovementValue.NoGain(), Value(Room(29), Piece(0, 1)));
        Assert.Equal(new RoomImprovementValue.NoGain(), Value(Room(35), Piece(0, 10)));
        Assert.False(Value(Room(35), Piece(0, 10)).Due);
    }

    [Fact]
    public void APieceThatReachesTheNextStageIsBuiltOnlyWhenItsOwnersGainBeatsItsCost()
    {
        var reached = Assert.IsType<RoomImprovementValue.Assessed>(Value(Room(29), Piece(5, 600)));
        Assert.True(reached.Gain > 0);
        Assert.Equal(10, reached.Cost, 9);
        // At a token material value the owner's gain outweighs the build; at a prohibitive one it does not.
        Assert.True(Value(Room(29), Piece(5, 600), Facts(materialValue: 0)).Due);
        Assert.False(Value(Room(29), Piece(5, 600), Facts(materialValue: 1e6)).Due);
    }

    [Fact]
    public void AnUnreadFactLeavesTheImprovementUnassessedAndUnplanned()
    {
        Assert.IsType<RoomImprovementValue.Unassessed>(Value(Room(29), null));
        Assert.IsType<RoomImprovementValue.Unassessed>(Value(Room(29), Piece(5, 600), owners: []));
        Assert.IsType<RoomImprovementValue.Unassessed>(Value(Room(29), Piece(5, 600), Facts() with { Values = null }));
        Assert.IsType<RoomImprovementValue.Unassessed>(Value(Room(29), Piece(5, 600) with { Costs = [new(new Resource("Steel"), 1)] }));
        Assert.IsType<RoomImprovementValue.Unassessed>(Value(Room(29), Piece(5, 600), Facts() with { Gains = Facts().Gains with { Pawns = new Dictionary<string, NeedsForecastPawn>() } }));
        Assert.All(new[] { Value(Room(29), null), Value(Room(29), Piece(5, 600), owners: []) }, v => Assert.False(v.Due));
    }
}
