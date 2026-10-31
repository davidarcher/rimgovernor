using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001 "Guests or prisoners need accommodation": a hosted guest with no vacant suitable bed stays a sleeping
/// target, so the shelter/sleeping Need stays unmet and the normal method builds, then assigns, a bed for it; only its
/// observed use of that bed meets the Need. Hosted guests and slaves size the station graph's sleep nodes and the
/// foothold sleeping places, never the colonists' table, joy or worker demand.
/// </summary>
public sealed class GuestAccommodationTests
{
    private static readonly PawnId A = new("a");
    private static readonly PawnId G = new("g");

    private static SleepingPerson Person(PawnId id, string bed = "") => new()
    {
        Native = SleepingNativeFixtures.Person(id),
        Pawn = SleepingNativeFixtures.Pawn(id, ownedBed: bed, comfortableMin: 10, comfortableMax: 30),
    };

    private static SleepingBed Bed(string id, EquatableArray<PawnId> owners = default, EquatableArray<PawnId> users = default) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: owners, users: users, accessibleTo: [A, G], room: "r1"),
        Building = SleepingNativeFixtures.Building(id, roofed: true),
        Humanlike = true,
        RestEffectiveness = 1,
    };

    /// <summary>The colonist sleeps in its own bed; the guest owns <paramref name="guestBed"/> or nothing.</summary>
    private static SleepingObservation Colony(string guestBed = "", params SleepingBed[] more) => new()
    {
        Colonists = 1,
        People = [Person(A, "bedA")],
        Guests = [Person(G, guestBed)],
        Beds = [Bed("bedA", owners: [A], users: [A]), .. more],
        BedBuildable = true,
    };

    private static SleepingRequest Request(SleepingObservation sleeping, SleepingReview review) => new()
    {
        Targets = review.Targets,
        Sleeping = sleeping,
        Rooms = RoomFixtures.Census((RoomFixtures.State("r1", temperatureC: 21), new[] { new Cell(1, 1), new Cell(2, 1) })),
        Definitions = [new BenchDefinition { Name = "Bed", Available = true }],
        Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] },
    };

    private static Need SleepingNeed(SleepingReview review) => ShelterRules.SleepingNeedOf(new ShelterFacts
    {
        SleepingRecovered = review.Recovered(),
        SleepingMin = 20.0,
        SleepingMax = 22.0,
        TemperatureOwed = false,
        HeatDoorOwed = false,
        SafeAreaOwed = SafeAreaStatus.Clear,
        ColdExit = 10,
        HotExit = 30,
    });

    [Fact]
    public void AGuestWithNoVacantBedStaysATargetAndHoldsTheSleepingNeed()
    {
        // Every bed is owned: the guest used to vanish from the targets, leaving the Need met.
        var review = SleepingRules.ReviewSleeping(Colony(), new SleepingHistory(), new Tick(600));
        var target = Assert.Single(review.Targets);
        Assert.Equal((G, SleepingNeedKind.Upgrade), (target.Pawn, target.Kind));
        Assert.Empty(target.Available);
        Assert.False(review.Recovered());
        Assert.Equal(Satisfaction.Unmet, SleepingNeed(review).Satisfaction);
    }

    [Fact]
    public void TheNormalMethodBuildsABedForTheGuestThenAssignsItAndOnlyObservedUseMeetsTheNeed()
    {
        var tick = new Tick(600);
        var bare = Colony();
        var review = SleepingRules.ReviewSleeping(bare, new SleepingHistory(), tick);
        var build = SleepingRules.SelectSleepingMethod(Request(bare, review));
        Assert.Equal((SleepingMethod.Build, 1, 1, "Bed"), (build.Method, build.Waiting, build.Unhoused, build.Definition));
        Assert.Equal(new[] { new Cell(1, 1), new Cell(2, 1) }, build.Cells);

        // The bed stands vacant: the guest is assigned to it.
        var built = Colony("", Bed("bedG"));
        review = SleepingRules.ReviewSleeping(built, review.History, tick);
        var assign = SleepingRules.SelectSleepingMethod(Request(built, review));
        Assert.Equal((SleepingMethod.Assign, G, "bedG", ""), (assign.Method, assign.Pawn, assign.Bed, assign.PreviousBed));
        Assert.Equal(Satisfaction.Unmet, SleepingNeed(review).Satisfaction);

        // Owned but not yet slept in: the assignment receipt does not meet the Need.
        var owned = Colony("bedG", Bed("bedG", owners: [G]));
        review = SleepingRules.ReviewSleeping(owned, review.History, tick);
        Assert.Equal((G, SleepingNeedKind.UseNeeded), (Assert.Single(review.Targets).Pawn, Assert.Single(review.Targets).Kind));
        Assert.Equal(SleepingMethod.NoDemand, SleepingRules.SelectSleepingMethod(Request(owned, review)).Method);
        Assert.Equal(Satisfaction.Unmet, SleepingNeed(review).Satisfaction);

        // Seen in the bed once, the guest is housed, and the exact-bed proof carries to later reviews.
        review = SleepingRules.ReviewSleeping(Colony("bedG", Bed("bedG", owners: [G], users: [G])), review.History, tick);
        Assert.True(review.Recovered());
        Assert.Equal(Satisfaction.Met, SleepingNeed(review).Satisfaction);
        Assert.Contains(review.History.Uses, u => u.Pawn == G && u.Bed == "bedG");
        Assert.True(SleepingRules.ReviewSleeping(owned, review.History, new Tick(1200)).Recovered());
    }

    [Fact]
    public void HostedGuestsAndSlavesSizeTheSleepNodesButNotTheColonistsDemand()
    {
        var colony = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild");
        // One guest and one slave arrive: SleepingObservation.Hosted counts both.
        var hosted = new SleepingObservation { Guests = [Person(G)], Slaves = [Person(new PawnId("s"))] }.Hosted;
        Assert.Equal(2, hosted);
        var input = colony.GraphInput();
        var arrived = (colony with { Hosted = hosted }).GraphInput();
        Assert.Equal((3, 0, 3), (input.Population.Colonists, input.Population.Hosted, input.Population.Sleepers));
        Assert.Equal((3, 2, 5), (arrived.Population.Colonists, arrived.Population.Hosted, arrived.Population.Sleepers));
        Assert.Equal(5, StationGraph.SleepGraph(arrived).Nodes.Where(n => n.Purpose == StationPurpose.Sleep).Sum(n => n.Count));

        // Everything but the beds is the colonists' demand, unchanged by the arrivals.
        var before = StationGraph.BuildStationGraph(input);
        var after = StationGraph.BuildStationGraph(arrived);
        static List<(string, int)> NonSleep(StationGraph g) => [.. g.Nodes.Where(n => n.Purpose != StationPurpose.Sleep).Select(n => (n.Id, n.Count))];
        Assert.Equal(NonSleep(before), NonSleep(after));
        Assert.Equal(before.Edges.Select(e => (e.A, e.B, e.Weight)), after.Edges.Select(e => (e.A, e.B, e.Weight)));

        // The arrival changes the population layout key, so the plan is regrown for it.
        static string Population(StationFacts f) => f.InputsKeyParts().Single(p => p.Key == "population").Value;
        Assert.NotEqual(Population(colony), Population(colony with { Hosted = 1 }));
    }

    [Fact]
    public void TheFootholdBedsTheHostedGuestButItIsNoWorkerOrFighter()
    {
        var housed = new ReviewFacts
        {
            Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 3, BedCapacity = 3, IndoorSleepingCapacity = 3 },
        };
        Assert.Equal(0, ShelterRules.StandInsOwed(housed).Missing);
        var hosting = housed with { Sleeping = new SleepingObservation { Guests = [Person(G)] } };
        // Three places for three colonists and a guest owe one stand-in spot and one indoor place.
        Assert.Equal((4L, 1), ShelterRules.StandInsOwed(hosting));
        Assert.Equal(4L, Rules.FootholdSleepers(hosting));
        Assert.False(Rules.FootholdSleeping(hosting));
        Assert.False(Rules.FootholdShelter(hosting));
        // The guest is not a colonist: the foothold's colonist count (the armed target's) stays three.
        Assert.Equal(3L, Rules.FootholdCount(hosting));
    }

    [Fact]
    public void ABedroomStepNeverPostponesTheBedAHostedGuestsShortageDemands()
    {
        var bare = Colony();
        var review = SleepingRules.ReviewSleeping(bare, new SleepingHistory(), new Tick(600));
        var request = Request(bare, review);
        var build = SleepingRules.SelectSleepingMethod(request);
        Assert.Equal(SleepingMethod.Build, build.Method);
        // The bedroom ladder houses colonists only: the guest's build goes ahead of any bedroom step.
        Assert.False(SleepingRules.BedroomsFirst(request, build));

        // A colonist owning no bed is the ladder's to house: its bedroom comes before a bedroll.
        var colonist = new SleepingObservation { Colonists = 1, People = [Person(A)], Beds = [], BedBuildable = true };
        var own = SleepingRules.ReviewSleeping(colonist, new SleepingHistory(), new Tick(600));
        var colonistRequest = Request(colonist, own);
        var colonistBuild = SleepingRules.SelectSleepingMethod(colonistRequest);
        Assert.Equal(SleepingMethod.Build, colonistBuild.Method);
        Assert.True(SleepingRules.BedroomsFirst(colonistRequest, colonistBuild));
        Assert.True(SleepingRules.BedroomsFirst(request, build with { Method = SleepingMethod.NoDemand }));
        Assert.False(SleepingRules.BedroomsFirst(request, build with { Method = SleepingMethod.Assign }));
    }
}
