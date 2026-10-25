using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using WireObjective = RimGovernor.Brain.GameFacts.Observations.QuestObjective;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The quest state row is total (#3862): acceptance, expiry, place, threat, decree asker and every objective's goal,
/// timing and mood floor are named states; the rewards are one row per option of the quest's reward choice and the
/// favor granted whatever is taken is quest-level. On the wire it round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class QuestStateTests
{
    /// <summary>A reward option that grants nothing.</summary>
    internal static readonly QuestReward NoReward = new([], 0, 0, 0, 0, []);

    /// <summary>An off-map offer with no rewards, objectives, shuttles, threat or expiry: the base every test quest row starts from.</summary>
    internal static readonly QuestState NoQuest = new()
    {
        Id = new("Quest1"),
        State = RimGovernor.Brain.GameFacts.Receipts.QuestStatus.NotYetAccepted,
        Acceptance = new QuestAcceptance.NotAccepted(),
        Expiry = new QuestExpiry.NoExpiry(),
        RequiresAccepter = false,
        CanAccept = true,
        EligiblePawns = [],
        Rewards = [],
        Favor = 0,
        ScriptDef = "OpportunitySite_ItemStash",
        Faction = new QuestFaction.NoFaction(),
        Place = new QuestPlace.OffMap(),
        Objectives = [],
        ChoicePartCount = 0,
        Asker = DecreeAsker.NotDecree,
        ViolentQuestsAllowed = true,
        Shuttles = [],
        Threat = new QuestThreat.NoThreat(),
        DeparturePawnIds = [],
    };

    /// <summary>An inactive objective of kind that counts nothing and names no pawn: the base every test objective starts from.</summary>
    internal static WireObjective Objective(QuestObjectiveKind kind) => new()
    {
        Kind = kind,
        Goal = new QuestObjectiveGoal.Uncounted(),
        Timing = new QuestObjectiveTiming.Untimed(),
        PawnIds = [],
        Active = false,
        MinimumMood = new QuestMoodFloor.NoFloor(),
        LodgerMoods = [],
    };

    /// <summary>A quest root the catalog classified as profile.</summary>
    internal static QuestRoot Root(string scriptDef, QuestProfile profile) =>
        new QuestRoot.Classified { ScriptDef = scriptDef, Profile = profile, SpaceLayer = "" };

    /// <summary>
    /// f with the quest census quests read: every root roots does not name is classified by its family table row
    /// (QuestFamilyForRoot), as the catalog does for a known root.
    /// </summary>
    internal static ReviewFacts WithQuests(ReviewFacts f, IEnumerable<QuestState> quests, params QuestRoot[] roots)
    {
        var rows = quests.ToList();
        var all = roots.ToList();
        foreach (var def in rows.Select(q => q.ScriptDef).Distinct(StringComparer.Ordinal))
        {
            if (!all.Any(r => r.ScriptDef == def))
            {
                all.Add(Root(def, QuestFamiliesPolicy.QuestFamilyForRoot(def)));
            }
        }
        return f with { QuestOffers = EquatableArray.From(rows), QuestRoots = EquatableArray.From(all) };
    }

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var quest = NoQuest with
        {
            State = RimGovernor.Brain.GameFacts.Receipts.QuestStatus.Ongoing,
            Acceptance = new QuestAcceptance.Accepted(1200),
            Expiry = new QuestExpiry.ExpiresIn(60000),
            EligiblePawns = [new PawnId("Thing_Human1")],
            Rewards = [new([new("Silver", 500)], 4, 10, 1, 2, ["SteelDrop"]), NoReward],
            Favor = 3,
            Faction = new QuestFaction.Involved(Hostile: false),
            Place = new QuestPlace.OnMap(0),
            Objectives =
            [
                new WireObjective
                {
                    Kind = QuestObjectiveKind.ProduceItem,
                    Goal = new QuestObjectiveGoal.Things("Gun_Revolver", null, 3, 1),
                    Timing = new QuestObjectiveTiming.Deadline(90000),
                    PawnIds = [],
                    Active = true,
                    MinimumMood = new QuestMoodFloor.NoFloor(),
                    LodgerMoods = [],
                },
                new WireObjective
                {
                    Kind = QuestObjectiveKind.HostLodgers,
                    Goal = new QuestObjectiveGoal.Pawns(1),
                    Timing = new QuestObjectiveTiming.Duration(30000),
                    PawnIds = [new PawnId("Thing_Human2")],
                    Active = false,
                    MinimumMood = new QuestMoodFloor.Floor(0.25),
                    LodgerMoods = [new(0.5)],
                },
            ],
            ChoicePartCount = 1,
            Asker = DecreeAsker.PlayerFaction,
            Threat = new QuestThreat.Points(350.5),
            DeparturePawnIds = [new PawnId("Thing_Human1")],
        };

        var json = OrdersJson.Serialize(quest);

        Assert.Equal(
            """{"id":"Quest1","state":"QUEST_STATUS_ONGOING","acceptance":{"type":"accepted","tick":1200},"expiry":{"type":"expires_in","ticks":60000},"requiresAccepter":false,"canAccept":true,"eligiblePawns":["Thing_Human1"],"rewards":[{"items":[{"defName":"Silver","units":500}],"favor":4,"goodwill":10,"psylink":1,"permitPoints":2,"permits":["SteelDrop"]},{"items":[],"favor":0,"goodwill":0,"psylink":0,"permitPoints":0,"permits":[]}],"favor":3,"scriptDef":"OpportunitySite_ItemStash","faction":{"type":"involved","hostile":false},"place":{"type":"on_map","mapId":0},"objectives":[{"kind":"produce_item","goal":{"type":"things","def":"Gun_Revolver","stuff":null,"count":3,"done":1},"timing":{"type":"deadline","tick":90000},"pawnIds":[],"active":true,"monument":null,"minimumMood":{"type":"no_floor"},"lodgerMoods":[],"workload":null,"gravEngine":null,"surveyScanner":null,"hackTargets":[],"giftRequest":null,"hackRisk":null},{"kind":"host_lodgers","goal":{"type":"pawns","count":1},"timing":{"type":"duration","ticks":30000},"pawnIds":["Thing_Human2"],"active":false,"monument":null,"minimumMood":{"type":"floor","mood":0.25},"lodgerMoods":[{"mood":0.5}],"workload":null,"gravEngine":null,"surveyScanner":null,"hackTargets":[],"giftRequest":null,"hackRisk":null}],"choicePartCount":1,"asker":"player_faction","violentQuestsAllowed":true,"shuttles":[],"threat":{"type":"points","value":350.5},"departurePawnIds":["Thing_Human1"]}""",
            json);
        Assert.Equal(quest, OrdersJson.Deserialize<QuestState>(json));
    }

    private static QuestState Offer(params QuestReward[] rewards) => NoQuest with
    {
        Id = new("q1"),
        ScriptDef = "ThreatReward_Raid_Joiner",
        Rewards = [.. rewards],
    };

    private static ReviewFacts Calm(params QuestState[] offers) => WithQuests(new ReviewFacts
    {
        Census = ColonyFactsCoreTests.Snapshot(),
        CurrentConstruction = CurrentConstruction.Empty,
        QuestCalm = QuestCalm.Settled,
        DefenseCapacity = 500.0,
    }, offers);

    [Fact]
    public void EachRewardOptionAddsItsOwnFavorToTheQuestFavor()
    {
        // Option 1 grants more favor; the quest-level favor counts for both.
        var offer = Offer(NoReward, NoReward with { Favor = 5 }) with { Favor = 7 };
        var choice = QuestRewards.SelectQuestReward(offer, Calm(offer));
        Assert.Equal((1, 12.0), (choice.Choice, choice.Value.Benefits));

        // A quest with no reward choice is scored on its quest favor alone, as choice -1.
        var single = QuestRewards.SelectQuestReward(Offer() with { Favor = 7 }, Calm());
        Assert.Equal((-1, 7.0), (single.Choice, single.Value.Benefits));

        Assert.Equal("reward_choices", QuestRewards.SelectQuestReward(offer with { ChoicePartCount = 2 }, Calm(offer)).Reason.Value);
    }

    [Fact]
    public void AQuestWithoutThreatPartsThreatensNoRaid()
    {
        var offer = Offer(NoReward);
        Assert.True(QuestSelection.JoinerThreatReason(offer, Calm(offer)).IsNone);
        var raid = offer with { Threat = new QuestThreat.Points(900) };
        Assert.Equal("defense_capacity", QuestSelection.JoinerThreatReason(raid, Calm(raid)).Value);
    }

    [Fact]
    public void ACrashOfferWithoutExpiryStaysOpen()
    {
        var offer = Offer();
        var rescue = WithQuests(Calm(), [offer], Root(offer.ScriptDef, new QuestProfile { Family = QuestFamily.ShuttleRescue }));
        Assert.True(QuestSelection.ShuttleRescueAdmission(offer, rescue).IsNone);
        Assert.True(QuestSelection.ShuttleRescueAdmission(offer with { Expiry = new QuestExpiry.ExpiresIn(2500) }, rescue).IsNone);
        Assert.Equal("expired", QuestSelection.ShuttleRescueAdmission(offer with { Expiry = new QuestExpiry.ExpiresIn(0) }, rescue).Value);
    }

    [Fact]
    public void OnlyAnActiveCountedDecreeObjectiveIsOwed()
    {
        var produce = Objective(QuestObjectiveKind.ProduceItem) with
        {
            Goal = new QuestObjectiveGoal.Things("Gun_Revolver", null, 3, 1),
            Active = true,
        };
        var decree = Offer() with
        {
            State = RimGovernor.Brain.GameFacts.Receipts.QuestStatus.Ongoing,
            ScriptDef = "Decree_ProduceItem",
            Objectives = [produce],
        };
        var f = Calm(decree);
        Assert.Equal(new DecreeDue { Objective = produce, Def = "Gun_Revolver", Stuff = "", Remaining = 2 }, QuestDecrees.DecreeObjective(decree, f));
        Assert.Null(QuestDecrees.DecreeObjective(decree with { Objectives = [produce with { Active = false }] }, f));
        Assert.Null(QuestDecrees.DecreeObjective(decree with { Objectives = [produce with { Goal = new QuestObjectiveGoal.Uncounted() }] }, f));
    }
}
