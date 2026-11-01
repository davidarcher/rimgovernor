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
        // The barn's ring door passes roamers: the barn alternative is priced (a plain shell door is ABarnEnteredByAPlainDoorIsNotCredited).
        AnimalDoors = [new PlannedRoom { Role = PlannedRole.Barn }.RingDefs().Door],
    };

    private static HerdShelterValue ValueOf(HerdShelter shelter, HerdShelterKind kind) => shelter.Alternatives.Single(a => a.Kind == kind).Value;

    private static LayoutPlan Plan() => new()
    {
        Zones = [new LayoutZone { Kind = LayoutZoningRules.ZoneCore, Runs = [.. Enumerable.Range(0, 80).Select(z => new RowRun(z, 0, 80))] }],
    };

    /// <summary>The barns the layout regroup compares against its no-barn baseline for these shelters on the plan.</summary>
    private static IReadOnlyList<BarnWant> Offered(LayoutPlan plan, params HerdShelter[] shelters) => LayoutHerdBarnRules.DeviationsOf(new StationFacts { Plan = plan, HerdShelters = shelters });

    [Fact]
    public void AColdExposedSusceptibleHerdIsOfferedABarnBesideItsPenAsADeviation()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2"))));
        var barn = Assert.IsType<HerdShelterValue.Assessed>(ValueOf(shelter, HerdShelterKind.Barn));
        Assert.Equal(HerdShelterRules.ReasonBarnAvoidsExposure, barn.Reason);
        Assert.True(barn.GainPerDay > 0);
        var wants = Offered(Plan(), shelter);
        Assert.Equal([new BarnWant("Cow", 2 * HerdShelterRules.BarnCellsPerAnimal)], wants);
        var nodes = LayoutPlan.HerdRoomNodes(Plan(), [new PenWant("Cow", 30)], wants);
        var pen = Assert.Single(nodes, n => n.Role == PlannedRole.Pen);
        var shed = Assert.Single(nodes, n => n.Role == PlannedRole.Barn);
        Assert.Equal((pen.Id, "Cow", HerdShelterRules.ReasonHerdShelter, 4), (shed.Beside, shed.Herd, shed.Reason, shed.Area));
    }

    [Fact]
    public void AStandingBarnOfTheUnitsCellsLeavesNoBarnToOffer()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2"))));
        var barn = new PlannedRoom { Id = 7, Role = PlannedRole.Barn, Herd = "Cow", Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 10, Z = 10, Width = 2, Height = 2 }) };
        Assert.Empty(Offered(Plan() with { Rooms = [barn] }, shelter));
        // A third cow outgrows it: the barn the deviation adds holds the whole unit, which the layout tops up past the standing one.
        var grown = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2"), Cow("cow3"))));
        Assert.Equal([new BarnWant("Cow", 3 * HerdShelterRules.BarnCellsPerAnimal)], Offered(Plan() with { Rooms = [barn] }, grown));
    }

    [Fact]
    public async Task TheBarnIsALayoutAlternativeTheSharedComparatorDecides()
    {
        // Bug: the barn must not be preselected by its own gain; the layout plans it only when the shared comparator picks it, whatever building it costs.
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2"))));
        var prepared = LayoutDeriveRules.FirstPlanFacts(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild").Survey, FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild"), []);
        var facts = prepared with { Pens = [new PenWant("Cow", 30)], HerdShelters = [shelter] };
        Assert.Empty(facts.Barns);
        var inputs = LayoutStationPlanRules.HostedRegroupInputsOf(facts);
        inputs = inputs with { Alternatives = [.. inputs.Alternatives.Select(h => h with { Input = h.Input with { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } } })] };
        var want = new BarnWant("Cow", 2 * HerdShelterRules.BarnCellsPerAnimal);
        var offered = Assert.Single(inputs.Alternatives, a => a.Barn == want);
        Assert.Equal(LayoutHerdBarnRules.IdOf(want), offered.Host.Id);
        static int Barns(Regrouping layout) => layout.Graph.Rooms.Count(r => r.Role == PlannedRole.Barn);

        // Valued like the bed test's costs: the barn applies when it is the cheaper outcome and is rejected when it is dearer.
        foreach (var (cost, applied) in new[] { (-10.0, true), (10.0, false) })
        {
            var result = await LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
                new RegroupValuation.Evaluated(candidate.Id, CareCapacityTests.Vector(Barns(candidate.Layout) * cost), []))]), TestContext.Current.CancellationToken);
            var decision = Assert.Single(result.Decisions, d => d.Kind == LayoutHerdBarnRules.BarnMove && d.B == LayoutHerdBarnRules.NameOf(want));
            Assert.Equal(LayoutHerdBarnRules.Barns, decision.A);
            Assert.Equal(applied, decision.Verdict == LayoutRegroupRules.RegroupApplied);
            Assert.Equal(applied ? 1 : 0, Barns(result));
        }
    }

    [Fact]
    public void ABarnEnteredByAPlainDoorIsNotCredited()
    {
        // Bug: the barn was credited with the deaths it stops while its ring door, the shell door, keeps roamers out.
        var plain = Input(Climate(Year(-30)), Cow("cow1"), Cow("cow2")) with { AnimalDoors = ["AnimalFlap"] };
        var shelter = Assert.Single(HerdShelterRules.Assess(plain));
        Assert.Equal(new HerdShelterValue.Unassessed(HerdShelterRules.FactAnimalAccess + "Cow"), ValueOf(shelter, HerdShelterKind.Barn));
        Assert.Empty(Offered(Plan(), shelter));
        Assert.Empty(ForecastHerdShelter.BarnCredit(new(1, "", 0), default!, [], Context() with { HerdShelter = plain }, 0));
    }

    [Fact]
    public void AComfortRangeThatCoversTheClimateIsAKnownZeroAndPlansNoBarn()
    {
        var shelter = Assert.Single(HerdShelterRules.Assess(Input(Climate(Year(15)), Cow("cow1"))));
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdShelterRules.ReasonComfortCoversClimate), ValueOf(shelter, HerdShelterKind.Barn));
        // A hardy herd: no barn is offered, so the baseline plans none.
        Assert.Empty(Offered(Plan(), shelter));
        Assert.DoesNotContain(LayoutPlan.HerdRoomNodes(Plan(), [new PenWant("Cow", 30)], Offered(Plan(), shelter)), n => n.Role == PlannedRole.Barn);
        // Exposure no def makes lethal is named, not priced: still no barn, under its own reason.
        var harmless = Input(Climate(Year(-30)), Cow("cow1"));
        harmless = harmless with { Penalties = harmless.Penalties with { Hypothermia = harmless.Penalties.Hypothermia with { Lethality = new Lethality.Harmless() } } };
        var mild = Assert.Single(HerdShelterRules.Assess(harmless));
        Assert.Equal(new HerdShelterValue.Assessed(0, HerdShelterRules.ReasonNonlethalExposure), ValueOf(mild, HerdShelterKind.Barn));
        Assert.Empty(Offered(Plan(), mild));
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
        Assert.Empty(Offered(Plan(), shelter));
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
        Assert.Empty(Offered(Plan(), shelter));
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
