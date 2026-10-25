using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3656: the bedroom step houses first the unhoused colonist with the largest priced gain from a bedroom, a flat-target
// gain read before the search, and the planned occupant the credit prices is that same colonist.
public sealed class BedroomOrderTests
{
    private const string Bed = "Bed";
    private static readonly PawnId A = new("a");
    private static readonly PawnId B = new("b");
    private static readonly NeedCell IndoorCell = new(true, false, false, false, false);

    // An unplanned room the game scores a Barracks: its beds house no one.
    private static readonly PlannedRoom Hall = new() { Id = 1, Region = RegionRules.RectRegion(new Policy.Rectangle(0, 0, 5, 5)) };
    private static readonly PlannedRoom Bedroom = new() { Id = 3, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Policy.Rectangle(20, 0, 4, 4)) };

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset)
        => new(thought, role, stage, offset, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false));

    // Barracks stage 2 (-4) to bedroom stage 2 (+2) is a delta of 6; an ascetic's stage-2 bedroom gives it the same 6.
    private static readonly NeedThoughtStage[] BedStages =
    [
        Stage("SleptInBedroom", "slept_in_bedroom", 2, 2), Stage("SleptInBarracks", "slept_in_barracks", 2, -4), Stage("Ascetic", "ascetic", 2, 6),
    ];

    /// <summary>A colonist owning hall bed <paramref name="owned"/> (a barracks at stage 2) who can also sleep in bedroom bed v (stage 2).</summary>
    private static NeedsForecastPawn Pawn(PawnId id, string owned, double target, double? threshold, bool ascetic = false) => NeedsFixtures.Pawn() with
    {
        Pawn = id.Value, Mood = threshold is { } minor ? NeedsFixtures.Mood with { UnclampedTarget = target, BreakThreshold = minor } : null, Ascetic = ascetic, OwnedBed = owned, Sleep = SleepFixtures.InBed(owned),
        ThoughtStages = EquatableArray.From(BedStages),
        Targets =
        [
            NeedsFixtures.Service(owned, "rest", 0d, 0) with { Bed = NeedsFixtures.BedRoom("Barracks", 2, false), Cell = IndoorCell },
            NeedsFixtures.Service("v", "rest", 0d, 0) with { Bed = NeedsFixtures.BedRoom("Bedroom", 2, false), Cell = IndoorCell },
        ],
    };

    private static BedroomGainFacts Gains(params NeedsForecastPawn[] pawns) => new()
    {
        Pawns = pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal),
        Thoughts = pawns.ToDictionary(p => p.Pawn, _ => EquatableArray.From(Array.Empty<MoodThought>()), StringComparer.Ordinal),
        Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
        WellbeingWeight = 0.1,
        PlannedStages = new Dictionary<int, RoomStage> { [Bedroom.Id] = new RoomStage.Staged(2) },
    };

    private static SleepingBed SleepingBed(string id, string room, params PawnId[] owners) => new()
    {
        Id = id, Definition = new(Bed), Humanlike = true, Room = room, Owners = [.. owners], AccessibleTo = [A, B],
    };

    private static SleepingPerson Person(PawnId id, string bed) => new() { Id = id, OwnedBed = Known(bed) };

    private static (RoomState Room, IEnumerable<Cell> Cells) Standing(string id, PlannedRoom room, RoomRole role, params string[] beds) => (RoomFixtures.State(id, role, beds: beds), room.Region.Cells());

    // Both colonists sleep in the hall, a barracks; the planned bedroom stands with its bed v vacant.
    private static readonly SleepingObservation Sharing = new()
    {
        Colonists = 2, People = [Person(A, "s1"), Person(B, "s2")],
        Beds = [SleepingBed("s1", "hall", A), SleepingBed("s2", "hall", B), SleepingBed("v", "bedroom")],
    };

    private static readonly RoomObservation StandingBedroom = RoomFixtures.Census(Standing("hall", Hall, RoomRole.Barracks, "s1", "s2"), Standing("bedroom", Bedroom, RoomRole.Bedroom, "v"));

    private static BedroomStep Step(SleepingObservation sleeping, BedroomGainFacts gains, Dictionary<string, RoomTarget>? targets = null) =>
        SleepingRules.NextBedroomStep(new LayoutPlan { Rooms = [Bedroom], NextRoomId = 4 }, StandingBedroom, sleeping, targets ?? [],
            new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>(), gains);

    [Fact]
    public void TheVacantBedroomBedGoesToTheColonistNearItsBreakThreshold()
    {
        // a is calm; b sits just under its break threshold, and the bedroom's +6 lifts it clear.
        var gains = Gains(Pawn(A, "s1", 0.8, 0.35), Pawn(B, "s2", 0.33, 0.35));
        Assert.True(gains.Gain("b", 2) > gains.Gain("a", 2));

        var move = Step(Sharing, gains);
        Assert.Equal((BedroomStepKind.Move, B, "v"), (move.Kind, move.Pawn, move.Bed));

        // Unbuilt, the planned bedroom's occupant is that same colonist, at the bedroom's predicted stage.
        var occupants = SleepingRules.PlannedBedroomOccupants(new LayoutPlan { Rooms = [Bedroom], NextRoomId = 4 },
            RoomFixtures.Census(Standing("hall", Hall, RoomRole.Barracks, "s1", "s2")), Sharing with { Beds = [.. Sharing.Beds.Take(2)] }, null, gains);
        Assert.Equal(["b"], Assert.Single(occupants, r => r.Key == Bedroom.Id).Value.ToArray());
    }

    [Fact]
    public void AnAsceticAndANonAsceticWithEqualGainsKeepIdOrder()
    {
        // a is an ascetic in the shared hall: its NeverUpgrade room houses it only when it owns that room alone.
        var gains = Gains(Pawn(A, "s1", 0.8, 0.35, ascetic: true), Pawn(B, "s2", 0.8, 0.35));
        Assert.Equal(gains.Gain("a", 2), gains.Gain("b", 2));
        var move = Step(Sharing, gains, new Dictionary<string, RoomTarget> { ["hall"] = new() { NeverUpgrade = true } });
        Assert.Equal((BedroomStepKind.Move, A, "v", 2), (move.Kind, move.Pawn, move.Bed, move.Unhoused));

        // Owning a NeverUpgrade room alone, the ascetic is housed and stays.
        var alone = Sharing with
        {
            People = [Person(A, "c1"), Person(B, "s2")],
            Beds = [SleepingBed("s1", "hall"), SleepingBed("s2", "hall", B), SleepingBed("v", "bedroom"), SleepingBed("c1", "cell", A)],
        };
        var kept = Step(alone, gains, new Dictionary<string, RoomTarget> { ["cell"] = new() { NeverUpgrade = true } });
        Assert.Equal((BedroomStepKind.Move, B, 1), (kept.Kind, kept.Pawn, kept.Unhoused));
    }

    [Fact]
    public void AnUnstagedRoomPricesNoGain()
    {
        // A staged room prices the gain at its stage; a room unstaged (its stuffs disagree, or no rest target reads the bed) prices none.
        var gains = Gains(Pawn(A, "s1", 0.8, 0.35), Pawn(B, "s2", 0.33, 0.35));
        Assert.Equal(gains.Gain("b", 2), gains.Gain("b", new RoomStage.Staged(2)));
        Assert.Null(gains.Gain("b", new RoomStage.Unstaged()));
        Assert.IsType<RoomStage.Unstaged>(gains.BedStage("b", "no-such-bed"));
    }

    [Fact]
    public void AnUnreadGainKeepsIdOrderAndIsNamed()
    {
        // b is near its break, but a has no mood need to price: id order, and a is named.
        var gains = Gains(Pawn(A, "s1", 0.8, null), Pawn(B, "s2", 0.33, 0.35));
        Assert.Null(gains.Gain("a", 2));
        Assert.Equal(A, Step(Sharing, gains).Pawn);

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var occupants = SleepingRules.PlannedBedroomOccupants(new LayoutPlan { Rooms = [Bedroom], NextRoomId = 4 },
            RoomFixtures.Census(Standing("hall", Hall, RoomRole.Barracks, "s1", "s2")), Sharing with { Beds = [.. Sharing.Beds.Take(2)] }, null, gains, findings);
        Assert.Equal(["a"], occupants[Bedroom.Id].ToArray());
        Assert.Equal(["bedroom_order_unpriced:a"], findings);
    }
}
