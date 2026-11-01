using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using QuestStatus = RimGovernor.Brain.GameFacts.Receipts.QuestStatus;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT prospective capacity (task 6.3): known arrivals, births, babies leaving the crib, the season's end and a chain's
/// later step are dated changes priced by one comparison against deferring. Preparation wins only for an assessed loss above its
/// cost, starts no earlier than its lead requires, and no empty-bed reserve sizes anything.
/// </summary>
public sealed class ProspectiveCapacityTests
{
    private const long Day = GameTime.TicksPerDay;
    private static readonly Tick Now = new(10 * Day);

    private static ExpectedChange Change(long at, double lossPerDay, ExpectedStay? stay = null) =>
        new("test", ExpectedOwner.Sleeper, 1, new ExpectedTiming.Dated(at), stay ?? new ExpectedStay.Permanent(), new ExpectedLoss.PerDay(lossPerDay));

    private static readonly ProvisionOption Bedroom = new("bedroom", false, LeadDays: 2, CostDays: 3);

    [Fact]
    public void AnArrivalPastTheLeadStartsAtItsLeadAndOneWithinTheLeadStartsNow()
    {
        // Arriving in 5 days, a 2-day build starts at the 3-day review, not now: it is not due yet.
        var later = Assert.IsType<ProspectiveChoice.Prepare>(ProspectiveCapacityRules.Choose(Change(Now.Value + 5 * Day, 1), Now, [Bedroom]));
        Assert.Equal(Now.Value + 3 * Day, later.StartTick);
        Assert.False(later.Due(Now));
        Assert.True(later.Due(new Tick(Now.Value + 3 * Day)));

        // Arriving in 1 day, an 8-day build starts now and pays the shortage from the arrival to readiness.
        var slow = Bedroom with { LeadDays = 8 };
        var now = Assert.IsType<ProspectiveChoice.Prepare>(ProspectiveCapacityRules.Choose(Change(Now.Value + Day, 1), Now, [slow]));
        Assert.Equal(Now.Value, now.StartTick);
        Assert.True(now.Due(Now));
        Assert.Equal(3 + 7, now.Price, 6);
    }


    [Fact]
    public void TwoLodgersInThreeDaysPrepareTheirBedsAtTheReviewTheLeadRequiresAndACancellationRetiresThem()
    {
        var arrival = Now.Value + 3 * Day;
        // One bed's work is one builder-day, so two lodgers' beds are a two-day lead: the build starts at day 1, not now, not never.
        ReviewFacts At(long tick, QuestStatus status, long departure, double priceDays) =>
            Hosting(Lodgers(status, new ExpectedTiming.Dated(arrival), new ExpectedTiming.Dated(departure), new PawnId("l1"), new PawnId("l2")),
                new SleepingObservation { Colonists = 1, People = [Person(new PawnId("a"), "bed-a")] }) with
            {
                Tick = new Tick(tick),
                ProspectiveBed = ProspectiveCapacityRules.BedOption(Day, priceDays, 1),
                Shapes = new PieceShapes { Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] } },
                SleepingBeds = [new SleepingBedFacts("Bed", "WoodLog", false, 1, 0.75)],
                ForecastObserved = new ColonyForecastObserved { Needs = NeedsFixtures.Read(tick, NeedsFixtures.Pawn("a") with
                {
                    Needs = [NeedsFixtures.Rate("rest", 0.8, 0.0001, 0.28) with { UnservedGainPerTick = 0.0001, GainPerTickPerEffectiveness = 0.0004 }],
                }) },
            };
        var month = arrival + 30 * Day;
        var probe = At(Now.Value, QuestStatus.Ongoing, month, 0.01);
        var change = Assert.Single(ProspectiveCapacityRules.SleepingChanges(probe, probe.SleepingBeds[0]));
        // The lodgers' shortage is the present colonist's rest loss without a bed, per lodger per day of the dated stay.
        var perDay = Assert.IsType<ExpectedLoss.PerDay>(change.Loss).CapabilityDays;
        Assert.True(perDay > 0);
        Assert.Equal(new ExpectedStay.Until(month), change.Stay);
        var cheap = perDay * 2 * 30 / 10;

        Assert.Equal(0, ProspectiveCapacityRules.Admitted(At(Now.Value, QuestStatus.Ongoing, month, cheap)).Sleepers);
        Assert.Equal(0, ProspectiveCapacityRules.Admitted(At(Now.Value + Day / 2, QuestStatus.Ongoing, month, cheap)).Sleepers);
        var due = ProspectiveCapacityRules.Admitted(At(Now.Value + Day, QuestStatus.Ongoing, month, cheap));
        Assert.Equal(2, due.Sleepers);
        // The admitted lodgers size two beds in the station graph through the normal Hosted path.
        Assert.Equal(2, LayoutStationPlanRules.StationPopulationOf(new StationFacts
        {
            Colonists = 1, Hosted = due.Sleepers, Patients = [], WorkPawns = new WorkPawnsRead([]),
        }).Hosted);

        // Cancelled after the build started: nobody is expected, the admission drops and the layout retires the beds.
        Assert.Equal(ProspectiveAdmission.None, ProspectiveCapacityRules.Admitted(At(Now.Value + Day, QuestStatus.EndedFailed, month, cheap)));

        // A one-day visit whose shortage (two lodgers, one day) is below two bedrooms' price defers.
        var visit = arrival + Day;
        var dear = perDay * 2 * 1;
        Assert.Equal(0, ProspectiveCapacityRules.Admitted(At(Now.Value + Day, QuestStatus.Ongoing, visit, dear)).Sleepers);
        var (_, deferred) = Assert.Single(ProspectiveCapacityRules.SleepingChoices(At(Now.Value + Day, QuestStatus.Ongoing, visit, dear)));
        Assert.Equal("defer", deferred.Verdict);
    }
    [Fact]
    public void ACancelledArrivalRetiresItsPreparation()
    {
        var here = new PawnId("l1");
        var coming = new PawnId("l2");
        var sleeping = new SleepingObservation { Colonists = 1, People = [Person(new PawnId("a"))], Guests = [Person(here)] };
        var accepted = Hosting(Lodgers(QuestStatus.Ongoing, new ExpectedTiming.Dated(Now.Value + 5 * Day), here, coming), sleeping);
        var expected = Assert.Single(ProspectiveCapacityRules.SleepingChanges(accepted, null));
        Assert.Equal((ExpectedOwner.Sleeper, 1d), (expected.Owner, expected.Units));
        Assert.Equal(new ExpectedTiming.Dated(Now.Value + 5 * Day), expected.At);

        // Ended (cancelled or failed) or merely offered: nobody is expected, so nothing is prepared and the layout drops it.
        foreach (var status in new[] { QuestStatus.EndedFailed, QuestStatus.NotYetAccepted })
        {
            var ended = Hosting(Lodgers(status, new ExpectedTiming.Dated(Now.Value + 5 * Day), here, coming), sleeping);
            Assert.Empty(ProspectiveCapacityRules.SleepingChanges(ended, null));
            Assert.Equal(ProspectiveAdmission.None, ProspectiveCapacityRules.Admitted(ended));
        }
    }

    [Fact]
    public void AFullColonyWithNoExpectedChangeBuildsNoSpareBed()
    {
        // Every colonist sleeps in its bed and nothing is expected: no reserve bed is admitted, the graph sizes exactly the colony.
        var a = new PawnId("a");
        var b = new PawnId("b");
        var facts = Colony(new SleepingObservation { Colonists = 2, People = [Person(a, "bed-a"), Person(b, "bed-b")] }) with
        {
            ProspectiveBed = Bedroom,
        };
        Assert.Empty(ProspectiveCapacityRules.SleepingChanges(facts, null));
        var admitted = ProspectiveCapacityRules.Admitted(facts);
        Assert.Equal(ProspectiveAdmission.None, admitted);
        var population = LayoutStationPlanRules.StationPopulationOf(new StationFacts
        {
            Colonists = 2, Hosted = admitted.Sleepers, Prisoners = admitted.PrisonSleepers, CribLeavers = admitted.CribLeavers, Patients = [], WorkPawns = new WorkPawnsRead([]),
        });
        Assert.Equal((2, 0, 0), (population.Colonists, population.Hosted, population.Prisoners));
    }

    [Fact]
    public void AnUnassessedLossDefersAndNamesTheFact()
    {
        var unassessed = Change(Now.Value + Day, 0) with { Loss = new ExpectedLoss.Unassessed("needs forecast pawn:l2") };
        var choice = Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.Choose(unassessed, Now, [Bedroom with { CostDays = 0 }]));
        Assert.Equal("needs forecast pawn:l2", choice.Reason);
        Assert.False(choice.Due(Now));

        // An undated change or stay is no cheaper: it too defers naming what is missing.
        Assert.Equal("arrival tick:q", Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.Choose(
            Change(0, 1) with { At = new ExpectedTiming.Unread("arrival tick:q") }, Now, [Bedroom])).Reason);
        Assert.Equal("stay end:q", Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.Choose(
            Change(Now.Value, 1, new ExpectedStay.Undated("stay end:q")), Now, [Bedroom])).Reason);

        // An assessed loss below the cost defers too: preparation wins only when the loss exceeds the cost.
        Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.Choose(Change(Now.Value + 5 * Day, 0.01), Now, [Bedroom]));
        // And a tie keeps deferring.
        Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.Choose(
            Change(Now.Value, 1, new ExpectedStay.Until(Now.Value + 3 * Day)), Now, [Bedroom with { LeadDays = 0 }]));
    }

    [Fact]
    public void ABedShareCoversAShortVisitAndABedroomARecruit()
    {
        var share = new ProvisionOption("bed_share", true, LeadDays: 0, CostDays: 0.1, ResidualPerDay: 0.2);
        var room = new ProvisionOption("bedroom", false, LeadDays: 2, CostDays: 4);
        var arrival = Now.Value + 3 * Day;

        // A two-day visit: the share's residual over two days is far cheaper than a bedroom.
        var visit = ProspectiveCapacityRules.Choose(Change(arrival, 1, new ExpectedStay.Until(arrival + 2 * Day)), Now, [share, room]);
        Assert.Equal("bed_share", Assert.IsType<ProspectiveChoice.Temporary>(visit).Kind);

        // A recruit stays for good: the share's residual over the horizon outweighs the bedroom's cost.
        var recruit = ProspectiveCapacityRules.Choose(Change(arrival, 1), Now, [share, room]);
        Assert.Equal("bedroom", Assert.IsType<ProspectiveChoice.Prepare>(recruit).Kind);
    }

    [Fact]
    public void APregnancyDatesACribAndTheMothersBedRestAtTheBirthTick()
    {
        var mother = new PawnId("mother");
        var birth = new ExpectedTiming.Dated(Now.Value + 4 * Day);
        var facts = Colony(new SleepingObservation { Colonists = 1, People = [Person(mother, labor: birth)] });
        var changes = ProspectiveCapacityRules.SleepingChanges(facts, null);
        Assert.Equal([(ExpectedOwner.Crib, birth), (ExpectedOwner.CareBed, birth)], changes.Select(c => (c.Owner, c.At)));
        // No crib def is catalogued (no Biotech) and no primary bed is read, so the birth prepares nothing ahead and names why.
        Assert.Equal([ProspectiveCapacityRules.CribDefAbsent, "primary bed rest effectiveness"], changes.Select(c => ((ExpectedLoss.Unassessed)c.Loss).Fact));
        Assert.Equal(new ExpectedStay.Undated("postpartum exhaustion duration"), changes[1].Stay);
        Assert.Equal(ProspectiveCapacityRules.CribDefAbsent, Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.SleepingChoices(facts with { ProspectiveBed = Bedroom })[0].Choice).Reason);
        Assert.Equal(ProspectiveAdmission.None, ProspectiveCapacityRules.Admitted(facts with { ProspectiveBed = Bedroom }));
        // The native unread state passes through by name.
        var unread = Colony(new SleepingObservation { Colonists = 1, People = [Person(mother, labor: new ExpectedTiming.Unread("birth tick:mother"))] });
        Assert.All(ProspectiveCapacityRules.SleepingChanges(unread, null), c => Assert.Equal(new ExpectedTiming.Unread("birth tick:mother"), c.At));
    }

    // The newborn rest rates the 1.7 defs give (DefinitionCatalog.NewbornRest): Need_Rest's 1.5833333E-05 fall and 3.809524E-05 gain
    // a tick, the ground's 0.8 effectiveness, and no HumanlikeBaby or Human rest stat modifier.
    private static readonly NewbornRestRates Newborn = new(1.5833333E-05, 3.809524E-05 * 0.8, 3.809524E-05);

    // A colony with a pregnant mother (birth at birthTick) and no baby yet, the mother with a rest row, the catalog's bed and
    // crib, the newborn's def-level rest rates, and the game's postpartum exhaustion (Biotech's HediffCompProperties_Disappears,
    // 40000 ticks).
    private static ReviewFacts Birth(long birthTick, ProvisionOption? crib)
    {
        var mother = new PawnId("mother");
        var rest = NeedsFixtures.Rate("rest", 0.8, 0.0001, 0.28) with { UnservedGainPerTick = 0.0001, GainPerTickPerEffectiveness = 0.0004 };
        return Colony(new SleepingObservation { Colonists = 1, People = [Person(mother, labor: new ExpectedTiming.Dated(birthTick))] }) with
        {
            ProspectiveBed = Bedroom,
            ProspectiveCrib = crib,
            NewbornRest = Newborn,
            Shapes = new PieceShapes { Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }], Crib = "Crib" } },
            SleepingBeds = [new SleepingBedFacts("Bed", "WoodLog", false, 1, 0.75), new SleepingBedFacts("Crib", "WoodLog", false, 1, 0.75)],
            CapacityPenalties = new CapacityPenaltyTables { PostpartumExhaustion = new HediffPenaltyTable { Known = true, Hediff = "PostpartumExhaustion", Lifespan = new Lifespan.EndsAfter(40000, 40000) } },
            ForecastObserved = new ColonyForecastObserved { Needs = NeedsFixtures.Read(Now.Value, NeedsFixtures.Pawn("mother") with { Needs = [rest] }) },
        };
    }

    [Fact]
    public void ABirthSixDaysOutPreparesACribOnlyWhenTheComparatorValuesIt()
    {
        var birth = Now.Value + 6 * Day;
        // A colony's first birth, no baby present: the newborn's loss is the rest the crib (effectiveness 1) frees over the ground
        // (0.8) at the def-level newborn rates (ForecastHousing.RestDaysFreed), with no needs row read for any baby.
        var unpriced = Birth(birth, null);
        Assert.DoesNotContain(unpriced.Sleeping.People, p => p.Baby);
        var loss = ProspectiveCapacityRules.SleepingChanges(unpriced).Single(c => c.Owner == ExpectedOwner.Crib).Loss;
        Assert.Equal(new ExpectedLoss.PerDay(ForecastHousing.RestDaysFreed(Newborn.FallPerTick, Newborn.UnservedGainPerTick, Newborn.GainPerTickPerEffectiveness)), loss);
        var perDay = Assert.IsType<ExpectedLoss.PerDay>(loss).CapabilityDays;
        Assert.True(perDay > 0);
        // Unread newborn rates name the defs' read.
        Assert.Equal(new ExpectedLoss.Unassessed(ProspectiveCapacityRules.NewbornRestUnread),
            ProspectiveCapacityRules.SleepingChanges(unpriced with { NewbornRest = null }).Single(c => c.Owner == ExpectedOwner.Crib).Loss);
        var cheap = ProspectiveCapacityRules.PieceOption("crib", Day, perDay * ColonyModelPolicy.DefaultHorizonDays / 2, 1);
        var choice = ProspectiveCapacityRules.SleepingChoices(Birth(birth, cheap)).Single(c => c.Change.Owner == ExpectedOwner.Crib).Choice;
        // A crib dearer than the horizon's shortage is not prepared; a cheap one is, started its one-day lead before the birth.
        var prepared = Assert.IsType<ProspectiveChoice.Prepare>(choice);
        Assert.Equal(("crib", birth - Day), (prepared.Kind, prepared.StartTick));
        Assert.False(choice.Due(Now));
        var dear = ProspectiveCapacityRules.PieceOption("crib", Day, perDay * ColonyModelPolicy.DefaultHorizonDays * 2, 1);
        Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.SleepingChoices(Birth(birth, dear)).Single(c => c.Change.Owner == ExpectedOwner.Crib).Choice);
        // No crib priced (none built yet, or no builder): the birth defers.
        Assert.IsType<ProspectiveChoice.Defer>(ProspectiveCapacityRules.SleepingChoices(Birth(birth, null)).Single(c => c.Change.Owner == ExpectedOwner.Crib).Choice);
        // A catalog without the crib def names the absent def.
        var absent = unpriced with { Shapes = new PieceShapes { Furniture = unpriced.Shapes.Furniture with { Crib = "" } } };
        Assert.Equal(new ExpectedLoss.Unassessed(ProspectiveCapacityRules.CribDefAbsent), ProspectiveCapacityRules.SleepingChanges(absent).Single(c => c.Owner == ExpectedOwner.Crib).Loss);
    }

    [Fact]
    public void TheMothersBedRestIsPricedByHerRestOverThePostpartumExhaustion()
    {
        var birth = Now.Value + 6 * Day;
        var facts = Birth(birth, null);
        var (change, choice) = ProspectiveCapacityRules.SleepingChoices(facts).Single(c => c.Change.Owner == ExpectedOwner.CareBed);
        // Her loss is her own rest the primary bed frees over the ground; the rest lasts the game's 40000-tick exhaustion.
        Assert.Equal(ProspectiveCapacityRules.RestLoss(facts, "mother", 1), change.Loss);
        var perDay = Assert.IsType<ExpectedLoss.PerDay>(change.Loss).CapabilityDays;
        Assert.True(perDay > 0);
        Assert.Equal(new ExpectedStay.Until(birth + 40000), change.Stay);
        // A two-day, three-day bedroom for two-thirds of a day's rest: the shortage over the exhaustion is priced and wins.
        var deferred = Assert.IsType<ProspectiveChoice.Defer>(choice);
        Assert.Equal(perDay * 40000 / Day, deferred.Shortage, 9);
        // A free, instant bed is prepared to be ready at the birth.
        var free = new ProvisionOption("bed", false, LeadDays: 0, CostDays: 0);
        Assert.IsType<ProspectiveChoice.Prepare>(ProspectiveCapacityRules.Choose(change, Now, [free]));
        // Without Biotech's hediff row the rest's end is unread and named.
        var unread = facts with { CapacityPenalties = new CapacityPenaltyTables() };
        Assert.Equal(new ExpectedStay.Undated("postpartum exhaustion duration"), ProspectiveCapacityRules.SleepingChanges(unread).Single(c => c.Owner == ExpectedOwner.CareBed).Stay);
    }

    [Fact]
    public void ABabyLeavingItsCribIsPricedByItsRestAndAdmittedOnlyOnceTheBedIsDue()
    {
        var baby = new PawnId("baby");
        ReviewFacts At(long leaves) => Colony(new SleepingObservation { Colonists = 1, People = [Person(baby, leavesCrib: new ExpectedTiming.Dated(leaves))] }) with
        {
            ProspectiveBed = Bedroom,
            Shapes = new PieceShapes { Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] } },
            SleepingBeds = [new SleepingBedFacts("Bed", "WoodLog", false, 1, 0.75)],
            ForecastObserved = new ColonyForecastObserved { Needs = NeedsFixtures.Read(Now.Value, NeedsFixtures.Pawn("baby") with
            {
                Needs = [NeedsFixtures.Rate("rest", 0.8, 0.0001, 0.28) with { UnservedGainPerTick = 0.0001, GainPerTickPerEffectiveness = 0.0004 }],
            }) },
        };
        var far = At(Now.Value + 20 * Day);
        var change = Assert.Single(ProspectiveCapacityRules.SleepingChanges(far, far.SleepingBeds[0]));
        Assert.IsType<ExpectedLoss.PerDay>(change.Loss);
        // Twenty days out, the two-day bed waits for its lead; two days out it is due and the baby sizes a bed.
        Assert.Equal(0, ProspectiveCapacityRules.Admitted(far).CribLeavers);
        Assert.Equal(1, ProspectiveCapacityRules.Admitted(At(Now.Value + 2 * Day)).CribLeavers);
        // Without a priced bed nothing is prepared.
        Assert.Equal(0, ProspectiveCapacityRules.Admitted(At(Now.Value + 2 * Day) with { ProspectiveBed = null }).CribLeavers);
    }

    [Fact]
    public void TheSeasonsEndDatesTheHayDemandAndAFieldIsSizedOnlyWhenReadyByThen()
    {
        var hay = new CropChoice { Name = "Hay", Harvests = SocialCropsRules.HayResource, Available = true, GrowDays = 4, HarvestNutrition = 1 };
        CropClimate Climate(double remaining) => new()
        {
            Census = ColonyFactsCoreTests.Snapshot() with { FoodClimate = ColonyFactsCoreTests.Snapshot().FoodClimate with { SowingNow = true, GrowingDaysRemaining = remaining } },
            OutdoorsDark = false,
        };
        var change = ProspectiveCapacityRules.HayChange(5, Climate(30), Now);
        Assert.Equal((ExpectedOwner.HayField, new ExpectedTiming.Dated(Now.Value + 30 * Day)), (change.Owner, change.At));
        Assert.Equal(new ExpectedTiming.Unread("growing season"), ProspectiveCapacityRules.HayChange(5, Climate(30) with { OutdoorsDark = true }, Now).At);

        Assert.True(AnimalFoodRules.PlanHayField(5, hay, Climate(30), Now, out var plan));
        Assert.Equal(5, plan.Needed);
        // Four grow days over the field budget's cycles do not fit in five remaining days: the field could not serve by the end.
        Assert.False(AnimalFoodRules.PlanHayField(5, hay, Climate(5), Now, out _));
    }

    [Fact]
    public void AChainsLaterStepRaisesDatedPowerDemandOnceItsBridgeStands()
    {
        var plan = new LayoutPlan
        {
            Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new("ElectricTailoringBench", "Make_Parka", 1), new("CraftingSpot", "Make_Parka", 1) { Step = HostStep.Bridge }] }],
        };
        double Draw(string def) => def == "ElectricTailoringBench" ? 120 : 0;
        var speeds = new Dictionary<string, BenchSpeedModel> { ["CraftingSpot"] = new(0.5, 0.5, []), ["ElectricTailoringBench"] = new(1, 1, []) };
        var recipes = new Dictionary<string, RecipeFlow> { ["Make_Parka"] = new RecipeFlow { Products = [new Amount(new Resource("Apparel_Parka"), 1)], Work = Day / 2 } };

        // The bridge stands: the table's draw is due now, and a day unpowered keeps the run at the spot's half speed: half a day's
        // work run twice as slowly costs another half day of labor.
        var due = Assert.Single(ProspectiveCapacityRules.ChainPower(plan, ["CraftingSpot"], [], Draw, speeds, recipes, Now));
        Assert.Equal((ExpectedOwner.Power, 120d, (ExpectedTiming)new ExpectedTiming.Dated(Now.Value)), (due.Owner, due.Units, due.At));
        Assert.Equal(new ExpectedLoss.PerDay(0.5), due.Loss);
        // An unread speed names the bench rather than pricing it.
        Assert.Equal(new ExpectedLoss.Unassessed("workbench speed:CraftingSpot"),
            Assert.Single(ProspectiveCapacityRules.ChainPower(plan, ["CraftingSpot"], [], Draw, new Dictionary<string, BenchSpeedModel>(), recipes, Now)).Loss);

        // Before the bridge stands the step's date is unread, so the prospective comparison cannot prepare it.
        var waiting = Assert.Single(ProspectiveCapacityRules.ChainPower(plan, [], [], Draw, speeds, recipes, Now));
        Assert.Equal(new ExpectedTiming.Unread("bridge stands:CraftingSpot"), waiting.At);

        // A table already standing or already pending (counted in the pending demand) is no expected change.
        Assert.Empty(ProspectiveCapacityRules.ChainPower(plan, ["CraftingSpot", "ElectricTailoringBench"], [], Draw, speeds, recipes, Now));
        Assert.Empty(ProspectiveCapacityRules.ChainPower(plan, ["CraftingSpot"], ["ElectricTailoringBench"], Draw, speeds, recipes, Now));
    }

    private static SleepingPerson Person(PawnId id, string bed = "", ExpectedTiming? labor = null, ExpectedTiming? leavesCrib = null) => new()
    {
        Native = SleepingNativeFixtures.Person(id, labor: labor, leavesCrib: leavesCrib),
        Pawn = SleepingNativeFixtures.Pawn(id, ownedBed: bed, comfortableMin: 10, comfortableMax: 30),
    };

    private static ReviewFacts Colony(SleepingObservation sleeping) => new() { Census = ColonyFactsCoreTests.Snapshot(), Sleeping = sleeping, Tick = Now };

    private static readonly QuestProfile Refugees = new() { Family = QuestFamily.HospitalityRefugee };

    private static QuestState Lodgers(QuestStatus state, ExpectedTiming arrival, params PawnId[] lodgers) => Lodgers(state, arrival, null, lodgers);

    private static QuestState Lodgers(QuestStatus state, ExpectedTiming arrival, ExpectedTiming? departure, params PawnId[] lodgers) => QuestStateTests.NoQuest with
    {
        Id = new("lodgers"),
        State = state,
        ScriptDef = "Hospitality_Refugee",
        Objectives = [QuestStateTests.Objective(QuestObjectiveKind.HostLodgers) with
        {
            Goal = new QuestObjectiveGoal.Pawns(lodgers.Length), PawnIds = [.. lodgers], Active = true, Arrival = arrival, Departure = departure,
        }],
    };

    private static ReviewFacts Hosting(QuestState quest, SleepingObservation sleeping) =>
        QuestStateTests.WithQuests(Colony(sleeping), [quest], QuestStateTests.Root("Hospitality_Refugee", Refugees));
}
