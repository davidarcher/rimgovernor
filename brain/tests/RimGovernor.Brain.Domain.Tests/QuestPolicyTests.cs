using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using Pb = RimGovernor.Contracts.Wire.Observations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The quest planner choices the deleted quest acceptance cases staged a live game for (#3404): a joiner offer accepted
/// with room and turned away without, a produce decree's outstanding count funded as a batch bill, a hospitality
/// quest's shuttle loaded then launched, and a title claim answered by its own bestowing quest.
/// </summary>
public sealed class QuestPolicyTests
{
    private static Pb.QuestState JoinerOffer(string quest, double threat = 100) => QuestStateTests.NoQuest with
    {
        Id = new(quest),
        ScriptDef = "ThreatReward_Raid_Joiner",
        Rewards = [QuestStateTests.NoReward, QuestStateTests.NoReward],
        Threat = new Pb.QuestThreat.Points(threat),
    };

    private static RoundsFacts Calm(params Pb.QuestState[] offers) => QuestStateTests.WithQuests(new RoundsFacts
    {
        Census = ColonyFactsCoreTests.Snapshot(),
        QuestCalm = QuestCalm.Settled,
        DefenseCapacity = 500.0,
        Population = PopulationFixtures.Census(),
        FoodDays = new FoodRunway.Days(20.0),
    }, offers);

    private static EquatableArray<Pb.QuestState> Offers(RoundsFacts f) => f.QuestOffers;

    [Fact]
    public void AJoinerOfferIsAcceptedWithRoomAndLeftWithoutIt()
    {
        var f = Calm(JoinerOffer("q9"), JoinerOffer("q2"));
        var capacity = PopulationRules.JoinerCapacity(f.JoinerCapacity());
        Assert.Equal(new PopulationCapacity.Assessed(true), capacity);

        // The lowest quest id, on its first reward choice.
        var choice = PopulationRules.SelectJoinerMethod(Offers(f), capacity, f);
        Assert.Equal((new QuestId("q2"), 0, JoinerPlanReason.None), (choice.Quest, choice.RewardChoice, choice.Reason));

        // A food runway under the floor is no room; a raid the colony cannot defend is turned away; an unread capacity is a missing fact.
        var full = f with { FoodDays = new FoodRunway.Days(1.0) };
        Assert.Equal(JoinerPlanReason.NoCapacity, PopulationRules.SelectJoinerMethod(Offers(full), PopulationRules.JoinerCapacity(full.JoinerCapacity()), full).Reason);
        var outgunned = Calm(JoinerOffer("q2", threat: 900));
        Assert.Equal(JoinerPlanReason.NoCapacity, PopulationRules.SelectJoinerMethod(Offers(outgunned), capacity, outgunned).Reason);
        Assert.Throws<RequiredFactException>(() => PopulationRules.SelectJoinerMethod(Offers(f), new PopulationCapacity.AwaitingFoodPlan(), f));
    }

    [Fact]
    public void JoinerCapacityNeedsNoUnownedBed()
    {
        // Every bed owned (#3749): the joiner sleeps on a stand-in spot and the next replan adds their bed.
        var owned = Calm() with
        {
            Sleeping = new SleepingObservation { Beds = [new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "bed", owners: [new PawnId("a")]), Building = SleepingNativeFixtures.Building(id: "bed"), Humanlike = true }] },
        };
        Assert.Equal(new PopulationCapacity.Assessed(true), PopulationRules.JoinerCapacity(owned.JoinerCapacity()));
    }

    private static readonly Pb.QuestObjective Produce = QuestStateTests.Objective(Pb.QuestObjectiveKind.ProduceItem) with
    {
        Goal = new Pb.QuestObjectiveGoal.Things("Gun_Revolver", null, 3, 1),
        Active = true,
    };

    private static readonly string[] RevolverIngredients = ["ComponentIndustrial", "Steel"];

    private static DecreeRecipe Revolver(bool existing = false) => new()
    {
        Bench = "bench-1",
        Recipe = "Make_Gun_Revolver",
        Product = new("Gun_Revolver"),
        Units = 1,
        Available = true,
        Existing = existing,
        Ingredients = [[new(new("Steel"), 30)], [new(new("ComponentIndustrial"), 2)]],
    };

    [Fact]
    public void AProduceDecreeOwesItsNativeCountAndFundsItAsABatchBill()
    {
        var offer = QuestStateTests.NoQuest with
        {
            Id = new("decree"),
            State = Receipts.QuestStatus.Ongoing,
            ScriptDef = "Decree_ProduceItem",
            Objectives = [Produce],
        };
        var f = Calm(offer);
        var due = QuestDecrees.DecreeObjective(offer, f);
        Assert.Equal(new DecreeDue { Objective = Produce, Def = "Gun_Revolver", Stuff = "", Remaining = 2 }, due);

        // Inventory is never progress: only an active objective with native production outstanding is owed.
        Assert.Null(QuestDecrees.DecreeObjective(offer with { State = Receipts.QuestStatus.NotYetAccepted }, f));
        Assert.Null(QuestDecrees.DecreeObjective(offer with { Objectives = [Produce with { Goal = new Pb.QuestObjectiveGoal.Things("Gun_Revolver", null, 3, 3) }] }, f));

        EquatableArray<Amount> stock = [new(new("Steel"), 100), new(new("ComponentIndustrial"), 10)];
        var (bill, reason) = QuestDecrees.PlanDecreeProduction(due!, [Revolver()], stock);
        Assert.Equal("", reason);
        Assert.Equal(("bench-1", "Make_Gun_Revolver", BillMode.GearBatch, 2), (bill!.Bench, bill.Recipe, bill.Mode, bill.Target));
        Assert.Equal(RevolverIngredients, bill.Ingredients);

        Assert.Equal("existing_work", QuestDecrees.PlanDecreeProduction(due!, [Revolver(existing: true)], stock).Reason);
        EquatableArray<Amount> scarce = [new(new("Steel"), 40), new(new("ComponentIndustrial"), 10)];
        Assert.Equal("production_infeasible", QuestDecrees.PlanDecreeProduction(due!, [Revolver()], scarce).Reason);
        Assert.Equal("complete", QuestDecrees.PlanDecreeProduction(due! with { Remaining = 0 }, [Revolver()], stock).Reason);
    }

    private static RoundsFacts Hosting(Pb.QuestShuttleState shuttle) => QuestStateTests.WithQuests(
        new RoundsFacts { Census = ColonyFactsCoreTests.Snapshot() },
        [QuestStateTests.NoQuest with { Id = new("hosting"), State = Receipts.QuestStatus.Ongoing, ScriptDef = "Hospitality_Animals", Shuttles = [shuttle] }],
        QuestStateTests.Root("Hospitality_Animals", new QuestProfile { Family = QuestFamily.HospitalityAnimals }));

    [Fact]
    public void AHospitalityShuttleIsAutoloadedThenLaunchedOnceLoaded()
    {
        var waiting = new Pb.QuestShuttleState
        {
            AutoloadAvailable = true,
            Autoload = false,
            Loading = false,
            AllRequiredLoaded = false,
            ManualLaunchAvailable = false,
            LoadedPawnIds = [],
            PendingPawnIds = [],
        };
        var load = QuestHospitality.SelectHospitalityWork(Hosting(waiting), new Tick(1)).Shuttle;
        Assert.Equal((new QuestId("hosting"), ShuttleLoading.Autoload, true, false), (load!.Quest, load.Loading, load.Autoload, load.Launch));

        Assert.True(QuestHospitality.SelectHospitalityWork(Hosting(waiting with { Autoload = true }), new Tick(1)) is { Shuttle: null, Waiting: true });

        var launch = QuestHospitality.SelectHospitalityWork(Hosting(waiting with { AllRequiredLoaded = true, ManualLaunchAvailable = true }), new Tick(1)).Shuttle;
        Assert.Equal((ShuttleLoading.LaunchOnly, true), (launch!.Loading, launch.Launch));
    }

    [Fact]
    public void ATitleClaimAnswersOnlyItsHoldersUnacceptedBestowingQuest()
    {
        var holder = new PawnId("pawn-1");
        BestowingCeremony Ceremony(string quest, string pawn, string title, bool accepted) =>
            new() { Quest = quest, Pawn = new(pawn), Title = title, Accepted = accepted, BestowerWaiting = false, Started = false };
        var royalty = new RoyaltyFacts
        {
            Ceremonies =
            [
                Ceremony("mine", "pawn-1", "Acolyte", accepted: false),
                Ceremony("accepted", "pawn-1", "Acolyte", accepted: true),
                Ceremony("other-pawn", "pawn-2", "Acolyte", accepted: false),
                Ceremony("other-title", "pawn-1", "Knight", accepted: false),
            ],
        };
        var claim = new TitleClaim { Holder = holder, Title = "Acolyte", Claim = true };

        Assert.Equal(new[] { new QuestId("mine") }, CeremonyRules.ClaimQuests(royalty, claim));
        Assert.Empty(CeremonyRules.ClaimQuests(royalty, claim with { Claim = false }));
    }

    private static readonly Pb.QuestSurveyScanner Scanner = new()
    {
        SiteId = "site-1",
        DurationTicks = 120_000,
        Scanner = Pb.SurveyScannerCondition.Intact,
        Hold = new Pb.SurveyHold.Running(new Tick(150_000)),
        Complete = false,
    };

    private static readonly Pb.QuestGravEngine Engine = new()
    {
        EngineId = "Thing_GravEngine7",
        Inspected = false,
        Place = new Pb.GravEnginePlace.Spawned(0, [new PawnId("b"), new PawnId("a")], []),
    };

    [Fact]
    public void SurveyAndGravEngineRecordsRoundTripExactly()
    {
        var scannerJson = WireJson.Serialize(Scanner);
        Assert.Equal(
            """{"siteId":"site-1","durationTicks":120000,"scanner":"intact","hold":{"type":"running","endTick":150000},"complete":false}""",
            scannerJson);
        Assert.Equal(Scanner, WireJson.Deserialize<Pb.QuestSurveyScanner>(scannerJson));

        var engineJson = WireJson.Serialize(Engine);
        Assert.Equal(
            """{"engineId":"Thing_GravEngine7","inspected":false,"place":{"type":"spawned","mapId":0,"eligiblePawnIds":["b","a"],"inspectingPawnIds":[]}}""",
            engineJson);
        Assert.Equal(Engine, WireJson.Deserialize<Pb.QuestGravEngine>(engineJson));
    }

    private static readonly RoundsFacts Surveying = new()
    {
        QuestObservedTick = new Tick(100_000),
    };

    private static Pb.QuestState SurveyOffer(Pb.QuestSurveyScanner scanner) => QuestStateTests.NoQuest with
    {
        Id = new("survey"),
        State = Receipts.QuestStatus.Ongoing,
        Objectives = [QuestStateTests.Objective(Pb.QuestObjectiveKind.HoldSurveyScanner) with { SurveyScanner = scanner }],
    };

    [Fact]
    public void ASurveyHoldReturnsOnNativeCompletionOrALostScannerAndWaitsWhileStopped()
    {
        var site = new Pb.WorldSite
        {
            Id = "site-1",
            State = Pb.WorldSiteState.Spawned,
            Place = new Pb.SitePlace.OnTile(5, 0),
            QuestIds = [new QuestId("survey")],
            Route = new Pb.SiteRoute.NoCrew(),
            Security = new Pb.SiteThreatBudget.Unbounded(),
        };
        SurveyPlan Work(Pb.QuestSurveyScanner scanner) =>
            QuestSurvey.SurveyWork(SurveyOffer(scanner), site, Surveying, new FoodSupply { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } }, RoundsRules.DefaultRoundsPolicy());

        var lost = Work(Scanner with { Scanner = Pb.SurveyScannerCondition.Destroyed });
        Assert.Equal((true, "scanner_destroyed"), (lost.Return, lost.Reason.Value));
        Assert.True(Work(Scanner with { Hold = new Pb.SurveyHold.Stopped(), Complete = true }).Return);
        Assert.Equal("survey_hold_stopped", Work(Scanner with { Hold = new Pb.SurveyHold.Stopped() }).Reason.Value);
        // A running hold reads on to the crew's supplies at the site.
        Assert.Equal("survey_supply_unknown", Work(Scanner).Reason.Value);
        // A second scanner naming the same site leaves the hold unread.
        var twice = SurveyOffer(Scanner) with { Objectives = [.. SurveyOffer(Scanner).Objectives, .. SurveyOffer(Scanner).Objectives] };
        Assert.Equal("survey_unknown", QuestSurvey.SurveyWork(twice, site, Surveying, new FoodSupply { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } }, RoundsRules.DefaultRoundsPolicy()).Reason.Value);
    }

    private static RoundsFacts Inspecting(Pb.QuestGravEngine? engine) => QuestStateTests.WithQuests(
        new RoundsFacts { Census = ColonyFactsCoreTests.Snapshot() },
        [
            QuestStateTests.NoQuest with
            {
                Id = new("signal"),
                State = Receipts.QuestStatus.Ongoing,
                Objectives = [QuestStateTests.Objective(Pb.QuestObjectiveKind.InspectGravEngine) with { GravEngine = engine }],
            },
        ]);

    [Fact]
    public void TheGravEngineIsInspectedByTheLowestEligibleColonistOnTheHomeMap()
    {
        var home = new MapId(0);
        var step = QuestGravInspection.SelectQuestGravInspection(Inspecting(Engine), home);
        Assert.Equal((new PawnId("a"), "Thing_GravEngine7", false), (step.Service!.Pawn, step.Service.Thing, step.Waiting));
        Assert.True(QuestGravInspection.GravInspectionDeficit(Inspecting(Engine)));

        // No single engine, an unspawned engine or one already being inspected waits; another map is skipped with a reason.
        Assert.True(QuestGravInspection.SelectQuestGravInspection(Inspecting(null), home) is { Service: null, Waiting: true });
        Assert.True(QuestGravInspection.SelectQuestGravInspection(Inspecting(Engine with { Place = new Pb.GravEnginePlace.Unspawned() }), home) is { Service: null, Waiting: true });
        Assert.True(QuestGravInspection.SelectQuestGravInspection(Inspecting(Engine with { Place = new Pb.GravEnginePlace.Spawned(0, [new PawnId("a")], [new PawnId("a")]) }), home) is { Service: null, Waiting: true });
        Assert.Equal("off_map", QuestGravInspection.SelectQuestGravInspection(Inspecting(Engine), new MapId(3)).Reason.Value);
        Assert.Equal("inspector_unavailable", QuestGravInspection.SelectQuestGravInspection(Inspecting(Engine with { Place = new Pb.GravEnginePlace.Spawned(0, [], []) }), home).Reason.Value);

        // An inspected engine is done.
        var done = Engine with { Inspected = true };
        Assert.True(QuestGravInspection.SelectQuestGravInspection(Inspecting(done), home).Quest.IsEmpty);
        Assert.False(QuestGravInspection.GravInspectionDeficit(Inspecting(done)));
    }
}
