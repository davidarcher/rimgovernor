using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001 (task 6.2): a pen unit's feeding, shelter and containment are deficits of its herd arrangement read off
/// observed service. The bug was that a pen marker and a fence were treated as the pen: bare ground fed nothing, a barn
/// was preselected by its own gain, and a hay field was asked for from the herd's raw demand whatever the pens grew.
/// </summary>
public sealed class HerdArrangementTests
{
    private static double[] Year(double t) => [.. Enumerable.Repeat(t, 12)];

    private static GearClimate Climate(double t) => new()
    {
        Temperatures = EquatableArray.From(Year(t)), DaylightHours = EquatableArray.From(Year(12)), CurrentTwelfth = 0, TicksToNextTwelfth = 5 * GameTime.TicksPerDay,
    };

    /// <summary>A penned (roped) animal of comfort range [min, max].</summary>
    private static UpkeepAnimal Animal(string id, string race = "Cow", double min = 0, double max = 35)
    {
        var a = AnimalFixtures.Owned(id, race);
        return a with
        {
            Pawn = a.Pawn with { Settings = a.Pawn.Settings with { Body = PawnFixtures.Body with { ComfortableMinC = min, ComfortableMaxC = max } } },
            Colony = a.Colony! with { Roping = new AnimalRoping.Roped(new() { Race = race, MtbDays = 2, MarketValue = 300, ReachesMapEdge = true }, null) },
        };
    }

    private static PlannedRoom Pen(int id, string herd, int x, int width = 6, int height = 5) => new()
    {
        Id = id, Role = PlannedRole.Pen, Herd = herd, Outdoor = true,
        Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = x, Z = 10, Width = width, Height = height }),
    };

    /// <summary>A marker's row; an enclosed one is bounded by fence "fence&lt;id&gt;" and gate "gate&lt;id&gt;" and holds <paramref name="inside"/>.</summary>
    private static PenGrazing Row(PlannedRoom pen, bool enclosed = true, double demand = 2, double today = 0, double worst = 0, double stored = 0, string[]? inside = null, params PenShelterRoom[] shelters) => new()
    {
        Boundary = enclosed ? ["fence" + pen.Id, "gate" + pen.Id] : [], Animals = enclosed ? EquatableArray.From(inside ?? []) : [],
        Marker = "marker" + pen.Id, Cell = pen.Region.Anchor(), Enclosed = enclosed, Cells = enclosed ? pen.Region.Bounds().Width * pen.Region.Bounds().Height : 0,
        DemandPerDay = enclosed ? demand : 0, PastureTodayPerDay = enclosed ? today : 0, PasturePerDay = enclosed ? worst : 0, StoredNutrition = enclosed ? stored : 0,
        Shelters = EquatableArray.From(shelters),
    };

    /// <summary>Every planned pen's ring fenced, less <paramref name="missing"/> cells of the first.</summary>
    private static GroundCensus Fenced(LayoutPlan plan, int missing = 0)
    {
        var ground = new GroundCensus();
        foreach (var pen in plan.Rooms)
        {
            ground.Fences.UnionWith(pen.WallRing().Cells());
        }
        foreach (var c in plan.Rooms.Take(1).SelectMany(r => r.WallRing().Cells()).Take(missing))
        {
            ground.Fences.Remove(c);
        }
        return ground;
    }

    private static HerdArrangementInput Input(double climate, LayoutPlan plan, PenGrazing[] rows, UpkeepAnimal[] animals, double? gap = 10, PenFeedStock[]? feed = null) => new()
    {
        Shelter = new()
        {
            Animals = animals, Herds = [new Resource("Cow"), new Resource("Muffalo")], Pens = [.. plan.Rooms.Select(r => new PenWant(r.Herd, 30))],
            Climate = Climate(climate), Penalties = ForecastValueTests.Tables(), AnimalSpot = "AnimalSleepingSpot", AnimalDoors = ["AnimalFlap"],
        },
        Plan = plan, Ground = Fenced(plan), Grazing = EquatableArray.From(rows), Feed = EquatableArray.From(feed ?? []), HarvestGap = gap is { } days ? new HarvestGap.Read(days) : new HarvestGap.Unread("growing season"),
    };

    private static readonly PlannedRoom CowPen = Pen(2, "Cow", 10);
    private static readonly LayoutPlan OnePen = new() { Rooms = [CowPen] };

    [Fact]
    public void AMarkerAndACompleteFenceOverBareGroundLeaveFeedingOpen()
    {
        var a = Assert.Single(HerdArrangementRules.Assess(Input(15, OnePen, [Row(CowPen)], [Animal("cow1")])));
        Assert.Equal((0, false, 0), (a.ContainmentDeficit, a.PenUnserved, a.ShelterDeficit));
        Assert.Equal((20.0, 20.0, 20.0), (a.FeedingDeficit, a.FeedNeed, a.HayNutrition));
        Assert.True(a.Open);
        Assert.Equal(HerdRemedyKind.HayField, a.Best(HerdDeficitKind.Feeding));
        Assert.Equal(HerdRemedyKind.Retain, a.Best(HerdDeficitKind.Containment));
        Assert.Equal("herd_arrangement:Cow:feeding=hay_field", a.State);
        Assert.Equal(new HerdShelterValue.Excluded(HerdArrangementRules.ReasonNoRingGap), a.Remedies.Single(r => r.Kind == HerdRemedyKind.RepairPen).Value);
        // The containment Need stays unmet on the open arrangement although every animal is inside a standing pen.
        var need = AnimalsFamilyRules.ContainmentNeedOf(new AnimalsFacts { Uncontained = EquatableArray.From<PawnId>([]), Pen = new() { Kind = HerdStepKind.None }, Arrangements = [a] });
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
    }

    [Fact]
    public void HayFeedsBackAsRunwayAndFeedingClosesOnlyWhenObservedNutritionCoversConsumption()
    {
        // The hay field's harvest hauled into the pen is the pen calculator's stockpiled nutrition: the runway.
        var shortOf = Assert.Single(HerdArrangementRules.Assess(Input(15, OnePen, [Row(CowPen, stored: 19)], [Animal("cow1")])));
        Assert.Equal((1.0, 20.0, 1.0), (shortOf.FeedingDeficit, shortOf.FeedNeed, shortOf.HayNutrition));
        Assert.True(shortOf.Open);
        var fed = Assert.Single(HerdArrangementRules.Assess(Input(15, OnePen, [Row(CowPen, stored: 20)], [Animal("cow1")])));
        Assert.Equal((0.0, 0.0), (fed.FeedingDeficit, HerdArrangementRules.HayDemand([fed])));
        Assert.False(fed.Open);
        Assert.Equal("herd_arrangement:Cow:served", fed.State);
        // The pen keeps its feed store while it eats past its pasture: the store is sized by the gross need, not the deficit.
        var feed = EquatableArray.From<PenFeedStock>([new() { Def = "Hay", Nutrition = 0, NutritionPerCell = 10 }]);
        Assert.Equal([new PenFeedAsk("Cow", CowPen.Region, ["Hay"], 2, false)], HerdArrangementRules.FeedAsks([fed], OnePen, feed));
    }

    [Fact]
    public void AnUnreadGrowingSeasonIsNamedAndKeepsTheArrangementOpen()
    {
        var a = Assert.Single(HerdArrangementRules.Assess(Input(15, OnePen, [Row(CowPen, stored: 100)], [Animal("cow1")], gap: null)));
        Assert.Equal(["growing season"], a.Unread);
        Assert.Equal("herd_arrangement:Cow:unread:growing season", a.State);
    }

    [Fact]
    public void AColdSpeciesClosesShelterOnlyOnAnObservedReachableRoofedRoomInItsBand()
    {
        UpkeepAnimal[] cows = [Animal("cow1"), Animal("cow2")];
        var open = Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5)], cows)));
        Assert.Equal(2, open.ShelterDeficit);
        // The barn the layout can raise has a plain shell door: it is unassessed, so the open baseline holds.
        Assert.Equal(new HerdShelterValue.Unassessed(HerdShelterRules.FactAnimalAccess + "Cow"), open.Remedies.Single(r => r.Kind == HerdRemedyKind.Barn).Value);
        Assert.Equal(HerdRemedyKind.Retain, open.Best(HerdDeficitKind.Shelter));
        // Were its ring door one roamers open, the barn would stop the deaths and win over the open ground.
        var passable = Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5)], cows);
        passable = passable with { Shelter = passable.Shelter with { AnimalDoors = [new PlannedRoom { Role = PlannedRole.Barn }.RingDefs().Door] } };
        var flap = Assert.Single(HerdArrangementRules.Assess(passable));
        Assert.IsType<HerdShelterValue.Assessed>(flap.Remedies.Single(r => r.Kind == HerdRemedyKind.Barn).Value);
        Assert.Equal((HerdRemedyKind.Barn, 2), (flap.Best(HerdDeficitKind.Shelter), flap.ShelterDeficit));
        // A roofed room the enclosure reaches but outside the band serves none; one too small serves what fits.
        var cold = new PenShelterRoom { Room = 5, Cells = 4, TemperatureC = -20 };
        Assert.Equal(2, Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5, shelters: cold)], cows))).ShelterDeficit);
        var small = new PenShelterRoom { Room = 5, Cells = 2, TemperatureC = 10 };
        Assert.Equal(1, Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5, shelters: small)], cows))).ShelterDeficit);
        var barn = new PenShelterRoom { Room = 5, Cells = 4, TemperatureC = 10 };
        var served = Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5, shelters: barn)], cows)));
        Assert.Equal((0, false), (served.ShelterDeficit, served.Open));
    }

    [Fact]
    public void AHardySpeciesHasNoShelterDeficitAndNoBarn()
    {
        var a = Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5)], [Animal("cow1", min: -60)])));
        Assert.Equal(0, a.ShelterDeficit);
        Assert.False(a.Open);
        Assert.Equal(HerdRemedyKind.Retain, a.Best(HerdDeficitKind.Shelter));
        Assert.DoesNotContain(a.Remedies, r => r.Kind == HerdRemedyKind.Barn && r.Value is HerdShelterValue.Assessed { GainPerDay: > 0 });
    }

    [Fact]
    public void AnUnreadComfortRangeIsNamedBySpecies()
    {
        var a = Assert.Single(HerdArrangementRules.Assess(Input(-30, OnePen, [Row(CowPen, today: 5, worst: 5)], [Animal("cow1", min: double.NaN)])));
        Assert.Contains(a.Unread, u => u.StartsWith("species comfort:", StringComparison.Ordinal));
        Assert.True(a.Open);
    }

    [Fact]
    public void AnUnenclosedPenIsAContainmentDeficitRepairedOverTheRetainBaselineByTheRoamingLossItStops()
    {
        UpkeepAnimal[] cows = [Animal("cow1")];
        var input = Input(15, OnePen, [Row(CowPen, enclosed: false)], cows) with { Ground = Fenced(OnePen, missing: 2), Uncontained = [cows[0].Id] };
        var a = Assert.Single(HerdArrangementRules.Assess(input));
        Assert.Equal((true, 1, 2), (a.PenUnserved, a.Uncontained, a.ContainmentDeficit));
        Assert.Equal(["pen nutrition:Cow"], a.Unread);
        // The baseline keeps the cow's roaming loss (300 over its 2-day mean time to leave); repair stops it.
        var loss = HerdShelterRules.LossPerDay(300, 2);
        Assert.True(loss > 0);
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdArrangementRules.ReasonRetain), a.Remedies.Single(r => r is { Deficit: HerdDeficitKind.Containment, Kind: HerdRemedyKind.Retain }).Value);
        Assert.Equal(new HerdShelterValue.Assessed(loss, HerdArrangementRules.ReasonRepairGaps), a.Remedies.Single(r => r.Kind == HerdRemedyKind.RepairPen).Value);
        // Repair restores the enclosure, so no new pen is weighed against it.
        Assert.Equal(new HerdShelterValue.Excluded(HerdArrangementRules.ReasonRepairRestores), a.Remedies.Single(r => r.Kind == HerdRemedyKind.NewPen).Value);
        Assert.Equal(HerdRemedyKind.RepairPen, a.Best(HerdDeficitKind.Containment));
        Assert.Equal("herd_arrangement:Cow:containment=repair_pen,unread:pen nutrition:Cow", a.State);

        // A large planned pen with no fence at all is still repaired: repair restores it.
        var wide = Pen(2, "Cow", 10, 12, 10);
        var bare = Input(15, new LayoutPlan { Rooms = [wide] }, [Row(wide, enclosed: false)], cows) with { Ground = new GroundCensus() };
        Assert.Equal(HerdRemedyKind.RepairPen, Assert.Single(HerdArrangementRules.Assess(bare)).Best(HerdDeficitKind.Containment));

        // No planned pen to repair: the new pen at the want stops the loss.
        var unplanned = Input(15, new LayoutPlan(), [], cows);
        unplanned = unplanned with { Shelter = unplanned.Shelter with { Pens = [new PenWant("Cow", 30)] } };
        var none = Assert.Single(HerdArrangementRules.Assess(unplanned));
        Assert.Equal((true, new HerdShelterValue.Assessed(loss, HerdArrangementRules.ReasonNewPen)), (none.PenUnserved, none.Remedies.Single(r => r.Kind == HerdRemedyKind.NewPen).Value));
        Assert.Equal(HerdRemedyKind.NewPen, none.Best(HerdDeficitKind.Containment));

        // An animal whose roaming loss is unpriced leaves every build unassessed: the baseline holds, it is not removed.
        var priceless = Animal("cow1") with { Colony = cows[0].Colony! with { Roping = new AnimalRoping.Roped(new() { Race = "Cow", MtbDays = 2, MarketValue = double.NaN, ReachesMapEdge = true }, null) } };
        var unpriced = Assert.Single(HerdArrangementRules.Assess(input with { Shelter = input.Shelter with { Animals = [priceless] } }));
        Assert.Equal(new HerdShelterValue.Unassessed("animal value:Cow"), unpriced.Remedies.Single(r => r.Kind == HerdRemedyKind.RepairPen).Value);
        Assert.Equal(HerdRemedyKind.Retain, unpriced.Best(HerdDeficitKind.Containment));

        // A marker alone, no fence: still unserved.
        var markerOnly = Input(15, OnePen, [Row(CowPen, enclosed: false)], cows) with { Ground = new GroundCensus() };
        Assert.True(Assert.Single(HerdArrangementRules.Assess(markerOnly)).PenUnserved);
    }

    [Fact]
    public void AdequateSummerPastureAsksNoHayField()
    {
        var a = Assert.Single(HerdArrangementRules.Assess(Input(15, OnePen, [Row(CowPen, demand: 2, today: 3, worst: 0)], [Animal("cow1")], gap: 0)));
        Assert.Equal((0.0, 0.0, 0.0), (a.FeedingDeficit, a.FeedNeed, a.HayNutrition));
        Assert.False(a.Open);
        Assert.Equal(0, HerdArrangementRules.HayDemand([a]));
        var feed = EquatableArray.From<PenFeedStock>([new() { Def = "Hay", Nutrition = 50, NutritionPerCell = 10 }]);
        Assert.Equal([new PenFeedAsk("Cow", CowPen.Region, [], 0, true)], HerdArrangementRules.FeedAsks([a], OnePen, feed));
    }

    [Fact]
    public void TwoPensCannotClaimTheSameFeedTwice()
    {
        var muffaloPen = Pen(3, "Muffalo", 30);
        var plan = new LayoutPlan { Rooms = [CowPen, muffaloPen] };
        var feed = new PenFeedStock[] { new() { Def = "Hay", Nutrition = 25, NutritionPerCell = 10 } };
        var arrangements = HerdArrangementRules.Assess(Input(15, plan, [Row(CowPen), Row(muffaloPen)], [Animal("cow1"), Animal("muff1", "Muffalo")], feed: feed));
        static double Haul(HerdArrangement a) => a.Remedies.Single(r => r.Kind == HerdRemedyKind.HaulFeed).Value is HerdShelterValue.Assessed { GainPerDay: var n } ? n : 0;
        Assert.Equal([("Cow", 20.0, 0.0), ("Muffalo", 5.0, 15.0)], arrangements.Select(a => (a.Herd, Haul(a), a.HayNutrition)));
        Assert.Equal(25, arrangements.Sum(Haul));
        Assert.Equal(15, HerdArrangementRules.HayDemand(arrangements));
    }

    [Fact]
    public void APenPieceIsHeldOnlyOnTheHerdWhosePenItBounds()
    {
        // The cows' pen is fed; the muffalo's is not. An old unplanned pen still holds a muffalo; a disused one holds nothing.
        var muffaloPen = Pen(3, "Muffalo", 30);
        var oldPen = Pen(4, "", 60);
        var disused = Pen(5, "", 80);
        var plan = new LayoutPlan { Rooms = [CowPen, muffaloPen] };
        var arrangements = HerdArrangementRules.Assess(Input(15, plan,
            [Row(CowPen, stored: 20), Row(muffaloPen), Row(oldPen, inside: ["muff1"]), Row(disused)], [Animal("cow1"), Animal("muff1", "Muffalo")]));
        Assert.Equal([("Cow", 0.0), ("Muffalo", 20.0)], arrangements.Select(a => (a.Herd, a.FeedingDeficit)));
        Assert.Equal(["fence2", "gate2", "marker2"], arrangements.Single(a => a.Herd == "Cow").Pieces);
        Assert.Equal(["fence3", "fence4", "gate3", "gate4", "marker3", "marker4"], arrangements.Single(a => a.Herd == "Muffalo").Pieces);

        // Only the unfed herd's pieces are held, its old pen's with them; the fed herd's and the disused pen's are not held on pens.
        string[] held = ["fence3", "gate3", "marker3", "fence4", "gate4", "marker4"], free = ["fence2", "gate2", "marker2", "fence5", "gate5", "marker5"];
        Assert.All(held, t => Assert.Equal("pen:Muffalo", TransitionBridgingRules.PenUnserved(arrangements, t)));
        Assert.All(free, t => Assert.Null(TransitionBridgingRules.PenUnserved(arrangements, t)));
    }
}
