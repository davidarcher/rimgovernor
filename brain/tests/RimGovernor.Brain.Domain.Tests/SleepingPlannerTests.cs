using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The sleeping planner's bedroom and suite steps (NextBedroomStep), formerly proven only by the native
/// sleeping/bedrooms and sleeping/suites cases (#3406): the first bedroom-wing room is shelled and a shelter sleeper is
/// moved into it, leaving the shelter bed; a Greedy colonist outgrowing a standard room gets a suite, and a title that
/// outgrows that suite gets a new, larger one while the first stays held.
/// </summary>
public sealed class SleepingPlannerTests
{
    private static readonly PawnId A = new("a");
    private static readonly PawnId B = new("b");

    private static Rectangle Rect(int x, int width, int height) => new() { X = x, Z = 0, Width = width, Height = height };

    private static PlannedRoom Planned(int id, PlannedRole role, Rectangle r) => new() { Id = id, Role = role, Region = RegionRules.RectRegion(r) };

    /// <summary>The planned room standing enclosed in the census, as a native Bedroom (a suite reads as one too).</summary>
    private static Room Standing(string id, PlannedRoom room, params string[] beds) => new()
    {
        Id = id, Role = Known(RoomRole.Bedroom), Enclosed = Known(true), Cells = [.. room.Region.Cells()], Beds = [.. beds],
    };

    private static SleepingBed Bed(string id, string room, params PawnId[] owners) => new()
    {
        Id = id, Definition = new("Bed"), Humanlike = true, Room = Known(room), Owners = [.. owners], AccessibleTo = [A, B],
    };

    private static SleepingPerson Person(PawnId id, string bed) => new() { Id = id, OwnedBed = Known(bed) };

    private static BedroomStep Step(LayoutPlan plan, Room[] rooms, SleepingObservation sleeping, Dictionary<string, RoomTarget>? targets = null) =>
        SleepingRules.NextBedroomStep(plan, new RoomObservation { Rooms = [.. rooms] }, sleeping, targets ?? [], new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>());

    // sleeping/bedrooms: a shelter for two, then two bedroom-wing rooms in plan order.
    private static readonly PlannedRoom Shelter = Planned(1, LayoutSurveyRules.PlannedShelter, Rect(0, 5, 5));
    private static readonly PlannedRoom Wing1 = Planned(2, LayoutSurveyRules.PlannedBedroom, Rect(10, 3, 4));
    private static readonly PlannedRoom Wing2 = Planned(3, LayoutSurveyRules.PlannedBedroom, Rect(14, 3, 4));
    private static readonly LayoutPlan Wing = new() { Rooms = [Shelter, Wing1, Wing2], NextRoomId = 4 };

    [Fact]
    public void OnceEveryoneOwnsAShelterBedTheFirstWingRoomIsShelledThenASleeperMovesInLeavingTheShelterBed()
    {
        var shelter = Standing("shelter", Shelter, "s1", "s2");
        var housed = new SleepingObservation
        {
            Colonists = 2,
            People = [Person(A, "s1"), Person(B, "s2")],
            Beds = [Bed("s1", "shelter", A), Bed("s2", "shelter", B)],
        };

        // Every colonist owns a bed, but only in the shelter: the plan's first bedroom-wing room is reconciled.
        var shell = Step(Wing, [shelter], housed);
        Assert.Equal(BedroomStepKind.Reconcile, shell.Kind);
        Assert.Equal(Wing1.Id, shell.Room.Id);
        Assert.Equal(2, shell.Unhoused);

        // Enclosed with a vacant bed in it: a shelter sleeper's ownership moves there, out of the shelter bed.
        var furnished = housed with { Beds = [.. housed.Beds, Bed("w1", "wing1")] };
        var move = Step(Wing, [shelter, Standing("wing1", Wing1, "w1")], furnished);
        Assert.Equal(BedroomStepKind.Move, move.Kind);
        Assert.Equal((A, "w1", "s1"), (move.Pawn, move.Bed, move.PreviousBed));

        // After the move the shelter bed stays standing: the next step builds the next room for the other sleeper,
        // and nothing clears the bed the colonist left.
        var moved = new SleepingObservation
        {
            Colonists = 2,
            People = [Person(A, "w1"), Person(B, "s2")],
            Beds = [Bed("s1", "shelter"), Bed("s2", "shelter", B), Bed("w1", "wing1", A)],
        };
        var next = Step(Wing, [shelter, Standing("wing1", Wing1, "w1")], moved);
        Assert.Equal(BedroomStepKind.Reconcile, next.Kind);
        Assert.Equal(Wing2.Id, next.Room.Id);
        Assert.NotEqual(BedroomStepKind.Clear, next.Kind);
    }

    // sleeping/suites: a 3x4 standard bedroom, a 7x7 suite sized for Greedy (SuiteSize(50)), and its 7x8 successor.
    private static readonly PlannedRoom Standard = Planned(1, LayoutSurveyRules.PlannedBedroom, Rect(0, 3, 4));
    private static readonly PlannedRoom Suite = Planned(2, LayoutTripsRules.PlannedSuite, Rect(10, 7, 7));
    private static readonly PlannedRoom Larger = Planned(3, LayoutTripsRules.PlannedSuite, Rect(20, 7, 8)) with { Replaces = 2 };

    private static UpkeepRoom Quality(string id, double impressiveness) =>
        new() { Id = id, Quality = Known(new RoomQuality(impressiveness, 0, 0, 0, impressiveness)) };

    private static RoomTarget Target(double min, params string[] reasons) => new() { Min = min, Reasons = [.. reasons] };

    [Fact]
    public void AGreedyColonistIsGivenASuiteAndATitleThatOutgrowsItGetsANewLargerSuiteWhileTheFirstStaysHeld()
    {
        Assert.Equal((7, 7), LayoutTripsRules.SuiteSize(50));
        var plan = new LayoutPlan { Rooms = [Standard, Suite], NextRoomId = 3 };
        var standard = Standing("std", Standard, "b1");
        var greedy = new SleepingObservation
        {
            Colonists = 1,
            People = [Person(A, "b1")],
            Beds = [Bed("b1", "std", A)],
            Rooms = Known<EquatableArray<UpkeepRoom>>([Quality("std", 30)]),
        };
        var greedyTargets = new Dictionary<string, RoomTarget> { ["std"] = Target(50, "greedy", "tier") };

        // The standard room cannot hold the floor Greedy needs: the suite is shelled first.
        var claim = Assert.Single(RoomQualityRules.SuiteClaims(plan, new RoomObservation { Rooms = [standard] }, greedy, greedyTargets, new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>()));
        Assert.Equal((A, SuiteClaimReason.Floor), (claim.Pawn, claim.Reason));
        var shell = Step(plan, [standard], greedy, greedyTargets);
        Assert.Equal((BedroomStepKind.Reconcile, Suite.Id), (shell.Kind, shell.Room.Id));

        // The suite stands with a bed: the colonist is moved into it out of the standard bed.
        var furnished = greedy with { Beds = [.. greedy.Beds, Bed("b2", "suite")] };
        var move = Step(plan, [standard, Standing("suite", Suite, "b2")], furnished, greedyTargets);
        Assert.Equal(BedroomStepKind.Move, move.Kind);
        Assert.Equal((A, "b2", "b1", Suite.Id), (move.Pawn, move.Bed, move.PreviousBed, move.Room.Id));

        // Baron's bedroom floor (70) outgrows the 7x7 suite: the claim is a new suite, and the first suite's wish keeps
        // its own floor, never a second wish that would grow it.
        var rooms = new RoomObservation { Rooms = [standard, Standing("suite", Suite, "b2")] };
        var inSuite = new SleepingObservation
        {
            Colonists = 1,
            People = [Person(A, "b2")],
            Beds = [Bed("b1", "std"), Bed("b2", "suite", A)],
            Rooms = Known<EquatableArray<UpkeepRoom>>([Quality("std", 30), Quality("suite", 55)]),
        };
        var baron = new Dictionary<string, RoomTarget> { ["suite"] = Target(70, "greedy", "tier", "title") };
        var outgrown = Assert.Single(RoomQualityRules.SuiteClaims(plan, rooms, inSuite, baron, new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>()));
        Assert.Equal((A, "b2", SuiteClaimReason.TooSmall), (outgrown.Pawn, outgrown.Bed, outgrown.Reason));
        Assert.Equal([new SuiteWish(70, Suite.Region.Area())], RoomQualityRules.SuiteWishes(plan, rooms, inSuite, baron, [outgrown]));

        // With the larger successor planned, it is the one shelled; the first suite is not touched and, slept in, stays
        // held where it stands, its plan row unchanged.
        var replanned = plan with { Rooms = [Standard, Suite, Larger], NextRoomId = 4 };
        var second = Step(replanned, [.. rooms.Rooms], inSuite, baron);
        Assert.Equal((BedroomStepKind.Reconcile, Larger.Id), (second.Kind, second.Room.Id));
        Assert.True(Larger.Region.Area() > Suite.Region.Area());
        var slept = SleepingRules.SleptBedrooms(replanned, Known(rooms), Known(inSuite));
        Assert.Equal([Suite.Id], slept);
        Assert.Contains(Suite.Id, LayoutPlan.TilePinned(replanned, new RoomGrowth { Fixed = [], Slept = slept }));

        // Once the larger suite stands with a bed, the colonist moves on out of the first suite's bed.
        var larger = inSuite with { Beds = [.. inSuite.Beds, Bed("b3", "larger")] };
        var onward = Step(replanned, [.. rooms.Rooms, Standing("larger", Larger, "b3")], larger, baron);
        Assert.Equal(BedroomStepKind.Move, onward.Kind);
        Assert.Equal((A, "b3", "b2", Larger.Id), (onward.Pawn, onward.Bed, onward.PreviousBed, onward.Room.Id));
    }

    /// <summary>
    /// upkeep/couple (#3434): once one partner owns the vacant double bed, the other is assigned to it on the next review,
    /// so nothing waits on a retry; partners native reports unwilling to share a bed (a spouse-only lovin' precept for
    /// lovers) are never paired, so the second has no bed to take and the fixture must stage a willing couple.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePartnerOfADoubleBedOwnerIsAssignedToItOnlyWhenBothAreWillingToShareABed(bool willing)
    {
        static SleepingPerson Partner(PawnId id, PawnId other, string bed, bool willing) => new()
        {
            Id = id, OwnedBed = Known(bed), ComfortableMin = Known(6.0), ComfortableMax = Known(30.0), Partners = [other], BedSharingAllowed = Known(willing),
        };
        var dbl = new SleepingBed
        {
            Id = "double", Definition = new("DoubleBed"), Humanlike = true, Roofed = true, RestEffectiveness = 1, Temperature = Known(28.0),
            Owners = [A], AccessibleTo = [A, B], Room = Known("42"),
        };
        var sleeping = new SleepingObservation
        {
            Colonists = 2, People = [Partner(A, B, "double", willing), Partner(B, A, "", willing)], Beds = [dbl], BedBuildable = Known(true),
        };

        var review = SleepingRules.ReviewSleeping(Known(sleeping), new SleepingHistory(), new Tick(600));
        var choice = SleepingRules.SelectSleepingMethod(new SleepingRequest { Targets = review.Targets, Sleeping = Known(sleeping) });

        if (willing)
        {
            Assert.Equal((SleepingMethod.Assign, B, "double"), (choice.Method, choice.Pawn, choice.Bed));
            return;
        }
        Assert.NotEqual(SleepingMethod.Assign, choice.Method);
    }
}
