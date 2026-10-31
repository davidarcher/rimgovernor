using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001 (task 2.3): where a pen unit's animals rest is compared, not ruled. Open pen ground is the baseline; a barn
/// beside the pen avoids the exposure deaths the seasonal climate deals animals outside their comfort range; an animal
/// sleeping spot fits every body size but changes no temperature. The bug was the categorical rule that no animal asks for a
/// roof: a herd freezing in its pen was never offered a barn.
/// </summary>
public sealed class HerdShelterTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double CowValue = 300;

    private static double[] Year(double t) => [.. Enumerable.Repeat(t, 12)];

    private static GearClimate Climate(params double[] curve) => new()
    {
        Temperatures = EquatableArray.From(curve), DaylightHours = EquatableArray.From(Year(12)), CurrentTwelfth = 0, TicksToNextTwelfth = 5 * GameTime.TicksPerDay,
    };

    /// <summary>A penned cow (roped, so RequiresPen) of comfort range [min, max].</summary>
    private static UpkeepAnimal Cow(string id, double min = 0, double max = 35, string race = "Cow")
    {
        var cow = AnimalFixtures.Owned(id, race);
        return cow with
        {
            Pawn = cow.Pawn with { Settings = cow.Pawn.Settings with { Body = PawnFixtures.Body with { ComfortableMinC = min, ComfortableMaxC = max } } },
            Colony = cow.Colony! with { Roping = new AnimalRoping.Roped(new() { Race = race, MtbDays = 2, MarketValue = CowValue, ReachesMapEdge = true }, null) },
        };
    }

    private static HerdShelterInput Input(GearClimate? climate, params UpkeepAnimal[] animals) => new()
    {
        Animals = animals, Herds = [new Resource("Cow")], Pens = [new PenWant("Cow", 30)], Climate = climate,
        Penalties = ForecastValueTests.Tables(), AnimalSpot = "AnimalSleepingSpot",
    };

    private static HerdShelterValue ValueOf(HerdShelter shelter, HerdShelterKind kind) => shelter.Alternatives.Single(a => a.Kind == kind).Value;

    private static LayoutPlan Plan() => new()
    {
        Zones = [new LayoutZone { Kind = LayoutZoningRules.ZoneCore, Runs = [.. Enumerable.Range(0, 80).Select(z => new RowRun(z, 0, 80))] }],
    };

    [Fact]
    public void AColdExposedSusceptibleHerdGeneratesABarnBesideItsPenThatWins()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2"))));
        var barn = Assert.IsType<HerdShelterValue.Assessed>(ValueOf(shelter, HerdShelterKind.Barn));
        Assert.Equal(HerdShelterRules.ReasonBarnAvoidsExposure, barn.Reason);
        Assert.True(barn.GainPerDay > 0);
        Assert.Equal(HerdShelterKind.Barn, shelter.Chosen);
        var wants = HerdShelterRules.BarnWants([shelter]);
        Assert.Equal([new BarnWant("Cow", 2 * HerdShelterRules.BarnCellsPerAnimal)], wants);
        var nodes = LayoutPlan.HerdRoomNodes(Plan(), [new PenWant("Cow", 30)], wants);
        var pen = Assert.Single(nodes, n => n.Role == PlannedRole.Pen);
        var shed = Assert.Single(nodes, n => n.Role == PlannedRole.Barn);
        Assert.Equal((pen.Id, "Cow", HerdShelterRules.ReasonHerdShelter, 4), (shed.Beside, shed.Herd, shed.Reason, shed.Area));
    }

    [Fact]
    public void AComfortRangeThatCoversTheClimateIsAKnownZeroAndPlansNoBarn()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(15)), Cow("cow1"))));
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdShelterRules.ReasonComfortCoversClimate), ValueOf(shelter, HerdShelterKind.Barn));
        Assert.Equal(HerdShelterKind.Open, shelter.Chosen);
        Assert.Empty(HerdShelterRules.BarnWants([shelter]));
        Assert.DoesNotContain(LayoutPlan.HerdRoomNodes(Plan(), [new PenWant("Cow", 30)], HerdShelterRules.BarnWants([shelter])), n => n.Role == PlannedRole.Barn);
        // Exposure no def makes lethal is named, not priced: still no barn, under its own reason.
        var harmless = Input(Climate(Year(-30)), Cow("cow1"));
        harmless = harmless with { Penalties = harmless.Penalties with { Hypothermia = harmless.Penalties.Hypothermia with { Lethality = new Lethality.Harmless() } } };
        var mild = Assert.Single(HerdShelterRules.Assess(harmless));
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdShelterRules.ReasonNonlethalExposure), ValueOf(mild, HerdShelterKind.Barn));
        Assert.Equal(HerdShelterKind.Open, mild.Chosen);
    }

    [Fact]
    public void ASmallSpeciesStillGetsTheSleepingSpotAlternative()
    {
        // The catalog's animal sleeping spot has no body-size limit, so a small race is offered it as a large one is.
        var small = Cow("hare1", race: "Snowhare");
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(15)), small)));
        Assert.Equal("", shelter.Herd);
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdShelterRules.ReasonBedNoThermalEffect), ValueOf(shelter, HerdShelterKind.Bed));
        Assert.Equal(new HerdShelterValue.Excluded(HerdShelterRules.ReasonNoAnimalSpot),
            ValueOf(Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(15)), small) with { AnimalSpot = "" })), HerdShelterKind.Bed));
    }

    [Fact]
    public void ABarnWithoutItsPenIsNotOffered()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1")) with { Pens = [] }));
        Assert.Equal(new HerdShelterValue.Excluded(HerdShelterRules.ReasonBarnWithoutPen), ValueOf(shelter, HerdShelterKind.Barn));
        Assert.Equal(HerdShelterKind.Open, shelter.Chosen);
        // A barn want with no pen node of its herd plans no barn: it would part the animals from their grazing.
        Assert.Empty(LayoutPlan.HerdRoomNodes(Plan(), [], [new BarnWant("Cow", 4)]));
    }

    [Fact]
    public void AnUnreadComfortRangeMakesOnlyTheBarnUnassessedNamingTheFact()
    {
        var blind = Cow("cow2", min: double.NaN);
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), blind)));
        Assert.Equal(new HerdShelterValue.Unassessed("animal comfort range:cow2"), ValueOf(shelter, HerdShelterKind.Barn));
        Assert.IsType<HerdShelterValue.Assessed>(ValueOf(shelter, HerdShelterKind.Open));
        Assert.IsType<HerdShelterValue.Assessed>(ValueOf(shelter, HerdShelterKind.Bed));
        Assert.Equal(HerdShelterKind.Open, shelter.Chosen);
        Assert.Equal("herd_shelter:Cow:barn:unassessed:animal comfort range:cow2", HerdShelterRules.StateOf(shelter.Herd, shelter.Alternatives[1]));
        Assert.Equal("herd_shelter:Cow:bed:excluded:no_animal_sleeping_spot",
            HerdShelterRules.StateOf("Cow", new(HerdShelterKind.Bed, new HerdShelterValue.Excluded(HerdShelterRules.ReasonNoAnimalSpot))));
        Assert.Equal(new HerdShelterValue.Unassessed("gear census"),
            ValueOf(Assert.Single(HerdShelterRules.Assess(Input(null, Cow("cow1")))), HerdShelterKind.Barn));
    }

    // ----- the ledger: a planned barn (room 1) beside the cows' pen (room 2), served from day three of ten.

    private static ForecastShelterPlan Shelter(double heat) => new(
        new(ForecastShelterSource.Planned, "1"), [new(10, 1)], [], ShelterOutcomes.Estimated(-30, -30, .001),
        heat == 0 ? [] : [new("heater1", new(10, 1), true, heat, 21, CoupledThermalTests.Powered(Day))], []);

    private static ForecastCoupledContext Context(double heat = 1, bool linked = true) => PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
    {
        PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Barn },
        LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Barn, Herd = "Cow", Region = RegionRules.RectRegion(new(10, 1, 1, 1)) } },
        PenHerds = new Dictionary<int, string> { [2] = "Cow" },
        PlannedLinks = linked ? [new RoomLink { A = 1, B = 2, Cell = new(10, 0) }] : [],
        HerdShelter = Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2")),
        Shelters = [Shelter(heat)],
    };

    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context) =>
        [.. ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, Day).InvestmentContributions.Where(c => c.Key.StartsWith("barn/", StringComparison.Ordinal))];

    [Fact]
    public void AHeatedBarnJoinedToItsPenIsCreditedTheDeathsItStops()
    {
        var context = Context();
        Assert.Equal(21, ForecastShelterPolicy.SteadyTemperature(context.Shelters[0]), 6);
        var rows = Credits(context);
        Assert.Equal(["barn/1/cow1", "barn/1/cow2"], rows.Select(r => r.Key));
        Assert.All(rows, r => Assert.True(r.Value < 0));
        var gains = ForecastHerdShelter.BarnCredit(new(1, "", 0), default!, [], context, 0);
        var planned = Assert.IsType<HerdShelterValue.Assessed>(ValueOf(Assert.Single(HerdShelterRules.Assess(context.HerdShelter)), HerdShelterKind.Barn));
        Assert.Equal(planned.GainPerDay, gains.Sum(g => g.PerDay), 12);
        // The served barn names each arrangement of its unit, so the comparison it won is read in the ledger's findings.
        Assert.Equal(
            [ForecastHerdShelter.FindingNonlethalUnpriced, ForecastHerdShelter.FindingRestUnpriced,
                "herd_shelter:Cow:open:open_baseline", "herd_shelter:Cow:barn:barn_avoids_exposure", "herd_shelter:Cow:bed:animal_bed_no_thermal_effect"],
            ForecastHerdShelter.Findings(new(1, "", 0), default!, [], context, 0));
    }

    [Fact]
    public void AnUnheatedOrUnjoinedBarnEarnsNothingAndAMissingShelterIsARequiredFact()
    {
        // An unheated barn settles at the outdoor temperature, so it stops no death.
        Assert.Empty(Credits(Context(heat: 0)));
        // A barn no link joins to its herd's pen holds no penned animal.
        Assert.Empty(Credits(Context(linked: false)));
        var thrown = Assert.Throws<RequiredFactException>(() => ForecastHerdShelter.BarnCredit(new(1, "", 0), default!, [], Context() with { Shelters = [] }, 0));
        Assert.Equal("barn shelter:1", thrown.Fact);
    }
}
