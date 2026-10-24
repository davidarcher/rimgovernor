using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The quest planner choices the deleted quest acceptance cases staged a live game for (#3404): a joiner offer accepted
/// with room and turned away without, a produce decree's outstanding count funded as a batch bill, a hospitality
/// quest's shuttle loaded then launched, and a title claim answered by its own bestowing quest.
/// </summary>
public sealed class QuestPolicyTests
{
    private static JoinerOffer JoinerOffer(string quest, double threat = 100) => new()
    {
        Quest = new(quest),
        ScriptDef = "ThreatReward_Raid_Joiner",
        State = "NotYetAccepted",
        CanAccept = true,
        ChoiceCount = 2,
        ThreatPoints = Known(threat),
    };

    private static RoundsFacts Calm(params JoinerOffer[] offers) => new()
    {
        QuestOffers = Known<EquatableArray<JoinerOffer>>([.. offers]),
        QuestColonyCalm = Known(true),
        DefenseCapacity = Known(500.0),
        Population = Known(PopulationFixtures.Census()),
        FoodDays = Known(20.0),
    };

    [Fact]
    public void AJoinerOfferIsAcceptedWithRoomAndLeftWithoutIt()
    {
        var f = Calm(JoinerOffer("q9"), JoinerOffer("q2"));
        var capacity = PopulationRules.JoinerCapacity(f.JoinerCapacity());
        Assert.True(capacity.TryGetValue(out var room) && room);

        // The lowest quest id, on its first reward choice.
        var choice = PopulationRules.SelectJoinerMethod(f.QuestOffers, capacity, f);
        Assert.Equal((new QuestId("q2"), 0, JoinerPlanReason.None), (choice.Quest, choice.RewardChoice, choice.Reason));

        // A food runway under the floor is no room; a raid the colony cannot defend is turned away; an unread census decides nothing.
        var full = f with { FoodDays = Known(1.0) };
        Assert.Equal(JoinerPlanReason.NoCapacity, PopulationRules.SelectJoinerMethod(full.QuestOffers, PopulationRules.JoinerCapacity(full.JoinerCapacity()), full).Reason);
        var outgunned = Calm(JoinerOffer("q2", threat: 900));
        Assert.Equal(JoinerPlanReason.NoCapacity, PopulationRules.SelectJoinerMethod(outgunned.QuestOffers, capacity, outgunned).Reason);
        Assert.Equal(JoinerPlanReason.CensusUnknown, PopulationRules.SelectJoinerMethod(default, capacity, f).Reason);
    }

    [Fact]
    public void JoinerCapacityNeedsNoUnownedBed()
    {
        // Every bed owned (#3749): the joiner sleeps on a stand-in spot and the next replan adds their bed.
        var owned = Calm() with
        {
            Sleeping = Known(new SleepingObservation { Beds = [new SleepingBed { Id = "bed", Humanlike = true, Owners = [new PawnId("a")] }] }),
        };
        Assert.Equal(Known(true), PopulationRules.JoinerCapacity(owned.JoinerCapacity()));
        Assert.Equal(Known(true), PopulationRules.JoinerCapacity((owned with { Sleeping = Unknown<SleepingObservation>() }).JoinerCapacity()));
    }

    private static readonly QuestObjective Produce = new()
    {
        Kind = Pb.QuestObjectiveKind.ProduceItem,
        Def = "Gun_Revolver",
        Count = Known(3L),
        Produced = Known(1L),
        Active = Known(true),
    };

    private static readonly string[] RevolverIngredients = ["ComponentIndustrial", "Steel"];

    private static DecreeRecipe Revolver(bool existing = false) => new()
    {
        Bench = "bench-1",
        Recipe = "Make_Gun_Revolver",
        Product = new("Gun_Revolver"),
        Units = 1,
        Available = Known(true),
        Existing = existing,
        Ingredients = [[new(new("Steel"), 30)], [new(new("ComponentIndustrial"), 2)]],
    };

    [Fact]
    public void AProduceDecreeOwesItsNativeCountAndFundsItAsABatchBill()
    {
        var offer = new JoinerOffer
        {
            Quest = new("decree"),
            State = "Ongoing",
            Profile = Known(QuestFamiliesPolicy.QuestFamilyForRoot("Decree_ProduceItem")),
            Objectives = [Produce],
        };
        Assert.Equal((Produce, 2L, true), QuestDecrees.DecreeObjective(offer));

        // Inventory is never progress: only an active objective with native production outstanding is owed.
        Assert.False(QuestDecrees.DecreeObjective(offer with { State = "NotYetAccepted" }).Owed);
        Assert.False(QuestDecrees.DecreeObjective(offer with { Objectives = [Produce with { Produced = Known(3L) }] }).Owed);

        Fact<EquatableArray<Amount>> stock = Known<EquatableArray<Amount>>([new(new("Steel"), 100), new(new("ComponentIndustrial"), 10)]);
        var (bill, reason) = QuestDecrees.PlanDecreeProduction(Produce, 2, [Revolver()], stock);
        Assert.Equal("", reason);
        Assert.Equal(("bench-1", "Make_Gun_Revolver", BillMode.GearBatch, 2), (bill!.Bench, bill.Recipe, bill.Mode, bill.Target));
        Assert.Equal(RevolverIngredients, bill.Ingredients);

        Assert.Equal("existing_work", QuestDecrees.PlanDecreeProduction(Produce, 2, [Revolver(existing: true)], stock).Reason);
        Fact<EquatableArray<Amount>> scarce = Known<EquatableArray<Amount>>([new(new("Steel"), 40), new(new("ComponentIndustrial"), 10)]);
        Assert.Equal("production_infeasible", QuestDecrees.PlanDecreeProduction(Produce, 2, [Revolver()], scarce).Reason);
        Assert.Equal("complete", QuestDecrees.PlanDecreeProduction(Produce, 0, [Revolver()], stock).Reason);
    }

    private static RoundsFacts Hosting(QuestShuttleState shuttle) => new()
    {
        QuestOffers = Known<EquatableArray<JoinerOffer>>(
        [
            new JoinerOffer
            {
                Quest = new("hosting"),
                State = "Ongoing",
                Profile = Known(new QuestProfile { Family = QuestFamily.HospitalityAnimals }),
                Shuttles = [shuttle],
            },
        ]),
    };

    [Fact]
    public void AHospitalityShuttleIsAutoloadedThenLaunchedOnceLoaded()
    {
        var waiting = new QuestShuttleState
        {
            AutoloadAvailable = Known(true),
            Autoload = Known(false),
            AllRequiredLoaded = Known(false),
            ManualLaunchAvailable = Known(false),
        };
        var load = QuestHospitality.SelectHospitalityWork(Hosting(waiting), new Tick(1)).Shuttle;
        Assert.Equal((new QuestId("hosting"), ShuttleLoading.Autoload, true, false), (load!.Quest, load.Loading, load.Autoload, load.Launch));

        Assert.True(QuestHospitality.SelectHospitalityWork(Hosting(waiting with { Autoload = Known(true) }), new Tick(1)) is { Shuttle: null, Waiting: true });

        var launch = QuestHospitality.SelectHospitalityWork(Hosting(waiting with { AllRequiredLoaded = Known(true), ManualLaunchAvailable = Known(true) }), new Tick(1)).Shuttle;
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
        QuestObservedTick = Known(new Tick(100_000)),
    };

    private static JoinerOffer SurveyOffer(Pb.QuestSurveyScanner scanner) => new()
    {
        Quest = new("survey"),
        State = "Ongoing",
        Objectives = [new QuestObjective { Kind = Pb.QuestObjectiveKind.HoldSurveyScanner, SurveyScanner = scanner }],
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
            QuestSurvey.SurveyWork(SurveyOffer(scanner), site, Surveying, default, RoundsRules.DefaultRoundsPolicy());

        var lost = Work(Scanner with { Scanner = Pb.SurveyScannerCondition.Destroyed });
        Assert.Equal((true, "scanner_destroyed"), (lost.Return, lost.Reason.Value));
        Assert.True(Work(Scanner with { Hold = new Pb.SurveyHold.Stopped(), Complete = true }).Return);
        Assert.Equal("survey_hold_stopped", Work(Scanner with { Hold = new Pb.SurveyHold.Stopped() }).Reason.Value);
        // A running hold reads on to the crew's supplies at the site.
        Assert.Equal("survey_supply_unknown", Work(Scanner).Reason.Value);
        // A second scanner naming the same site leaves the hold unread.
        var twice = SurveyOffer(Scanner) with { Objectives = [.. SurveyOffer(Scanner).Objectives, .. SurveyOffer(Scanner).Objectives] };
        Assert.Equal("survey_unknown", QuestSurvey.SurveyWork(twice, site, Surveying, default, RoundsRules.DefaultRoundsPolicy()).Reason.Value);
    }

    private static RoundsFacts Inspecting(Pb.QuestGravEngine? engine) => new()
    {
        QuestOffers = Known<EquatableArray<JoinerOffer>>(
        [
            new JoinerOffer
            {
                Quest = new("signal"),
                State = "Ongoing",
                Objectives = [new QuestObjective { Kind = Pb.QuestObjectiveKind.InspectGravEngine, GravEngine = engine }],
            },
        ]),
    };

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
