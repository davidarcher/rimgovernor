using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3864: the quest monument, hack target, hack risk and gift request rows are total records whose lifecycle is a
// named state, so the policy reads the state instead of a nullable field.
public sealed class QuestMonumentHackGiftTests
{
    private static RimGovernor.Brain.GameFacts.Observations.QuestMonumentPiece Piece(RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite site) => new()
    {
        DefName = "Wall", Offset = new Cell(1, 0), Rotation = Rotation.East, Footprint = [new Cell(0, 0)], Site = site,
        Options = [new RimGovernor.Brain.GameFacts.Observations.QuestMonumentBuildOption { Stuff = "Steel", Costs = [new RimGovernor.Brain.GameFacts.Observations.QuestMonumentMaterial("Steel", 5)], Work = 135 }],
    };

    private static RimGovernor.Brain.GameFacts.Observations.QuestMonument Monument(RimGovernor.Brain.GameFacts.Observations.QuestMonumentState state, RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite site, params RimGovernor.Brain.GameFacts.Observations.QuestMonumentResource[] resources) => new()
    {
        MarkerId = "MonumentMarker1", DefName = "MonumentMarker", MapId = 0, State = state, Pieces = [Piece(site)],
        Resources = [.. resources], AvailableResources = [new RimGovernor.Brain.GameFacts.Observations.QuestMonumentMaterial("Steel", 40)],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Installed Standing = new(new Cell(10, 10), false, null);

    private static readonly RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Placed Open = new(false, false, true);

    private static RimGovernor.Brain.GameFacts.Observations.QuestState Ongoing(string script, RimGovernor.Brain.GameFacts.Observations.QuestObjective objective) => QuestStateTests.NoQuest with
    {
        Id = new("q1"), ScriptDef = script, State = RimGovernor.Brain.GameFacts.Receipts.QuestStatus.Ongoing, Objectives = [objective],
    };

    private static RimGovernor.Brain.GameFacts.Observations.QuestObjective Objective => QuestStateTests.Objective(RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.ProduceItem);

    private static ReviewFacts Facts(RimGovernor.Brain.GameFacts.Observations.QuestState offer, QuestFamily family) =>
        QuestStateTests.WithQuests(new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() }, [offer], QuestStateTests.Root(offer.ScriptDef, new QuestProfile { Family = family }));

    private static QuestMonumentMethod Select(RimGovernor.Brain.GameFacts.Observations.QuestMonument marker) => QuestMonumentPolicy.SelectQuestMonument(
        [Ongoing("BuildMonument", Objective with { Monument = marker })],
        new MapId(0), new Dictionary<Resource, long> { [new Resource("Steel")] = 100 }, []);

    [Fact]
    public void EveryMonumentStateRoundTripsExactly()
    {
        RimGovernor.Brain.GameFacts.Observations.QuestMonumentState[] states =
        [
            new RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Offered([new Cell(3, 3)], [new RimGovernor.Brain.GameFacts.Observations.QuestMonumentMaterial("Steel", 50)]),
            new RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Packed([new Cell(3, 3)]),
            new RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Installed(new Cell(10, 10), true, "Wall9"),
        ];
        foreach (var state in states)
        {
            var marker = Monument(state, state is RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Installed ? Open : new RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Unplaced(),
                new RimGovernor.Brain.GameFacts.Observations.QuestMonumentResource { Id = "Steel1", DefName = "Steel", Cell = new Cell(4, 4), EligibleHaulers = [new PawnId("Human1")] });
            var json = FactsJson.Serialize(marker);
            var back = FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestMonument>(json);
            Assert.Equal(json, FactsJson.Serialize(back));
            Assert.Equal(marker, back);
        }
    }

    [Fact]
    public void HackAndGiftRowsRoundTripExactly()
    {
        RimGovernor.Brain.GameFacts.Observations.QuestHackTarget[] targets =
        [
            new() { Id = "Terminal1", LockedOut = false, Autohack = false, Satisfied = false, Place = new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.OnMap(0, [new PawnId("Human1")]) },
            new() { Id = "Terminal2", LockedOut = true, Autohack = false, Satisfied = false, Place = new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.Unspawned() },
        ];
        foreach (var target in targets)
        {
            var json = FactsJson.Serialize(target);
            Assert.Equal(target, FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestHackTarget>(json));
        }
        RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk[] asks =
        [
            new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Away(),
            new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.NotAsking(),
            new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Asking(0, 20, [new PawnId("Human2")], [new PawnId("Human1")]),
        ];
        foreach (var ask in asks)
        {
            var gift = new RimGovernor.Brain.GameFacts.Observations.QuestGiftRequest { Recipient = new PawnId("Beggar1"), DefName = "MedicineIndustrial", Pawns = [new PawnId("Beggar1")], Ask = ask };
            var json = FactsJson.Serialize(gift);
            Assert.Equal(json, FactsJson.Serialize(FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestGiftRequest>(json)));
            Assert.Equal(gift, FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestGiftRequest>(json));
        }
        var risk = new RimGovernor.Brain.GameFacts.Observations.QuestHackRisk { Hostile = true };
        Assert.Equal(risk, FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestHackRisk>(FactsJson.Serialize(risk)));
    }

    [Fact]
    public void TheMonumentPlanFollowsTheMarkerState()
    {
        // A packed marker installs at its first unprotected legal cell; an offered one still waits in its drop pods.
        var packed = Select(Monument(new RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Packed([new Cell(3, 3)]), new RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Unplaced()));
        Assert.Equal(("install", new Cell(3, 3)), (packed.Kind, packed.Move!.Cell));
        Assert.Equal("marker_unavailable", Select(Monument(new RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Offered([new Cell(3, 3)], []), new RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Unplaced())).Reason);

        // An installed marker hauls loose material first, then builds its open piece at the marker offset.
        var loose = new RimGovernor.Brain.GameFacts.Observations.QuestMonumentResource { Id = "Steel1", DefName = "Steel", Cell = new Cell(4, 4), EligibleHaulers = [new PawnId("Human2"), new PawnId("Human1")] };
        var haul = Select(Monument(Standing, Open, loose));
        Assert.Equal(("haul", new PawnId("Human1")), (haul.Kind, haul.Haul!.Pawn));
        var build = Select(Monument(Standing, Open));
        Assert.Equal(("build", new Cell(11, 10), Rotation.East, "Steel"), (build.Kind, build.Build!.Cell, build.Build.Rotation, build.Build.Stuff));

        // A complete marker is protected, a disallowed building blocks it, and an unplaced piece on an installed marker is a broken read.
        Assert.Equal("protect", Select(Monument(Standing with { AllDone = true }, Open)).Kind);
        Assert.Equal("disallowed_building", Select(Monument(Standing with { DisallowedBuildingId = "Wall9" }, Open)).Reason);
        Assert.Throws<RequiredFactException>(() => Select(Monument(Standing, new RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Unplaced())));
        Assert.Equal("wait", Select(Monument(Standing, new RimGovernor.Brain.GameFacts.Observations.QuestMonumentPieceSite.Placed(true, false, true))).Kind);
    }

    [Fact]
    public void AHackTargetIsDesignatedOnlyWhenItStandsOnThisMap()
    {
        static QuestHackWork Hack(RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace place) => QuestHackPolicy.SelectQuestHack(Facts(Ongoing("Hack_Spacedrone", Objective with
        {
            HackTargets = [new RimGovernor.Brain.GameFacts.Observations.QuestHackTarget { Id = "Terminal1", LockedOut = false, Autohack = false, Satisfied = false, Place = place }],
        }), QuestFamily.Hack), new MapId(0));

        Assert.Equal("Terminal1", Hack(new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.OnMap(0, [new PawnId("Human1")])).Hack!.Target);
        Assert.Equal("hacker_unavailable", Hack(new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.OnMap(0, [])).Reason.Value);
        Assert.Equal("off_map", Hack(new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.OnMap(4, [new PawnId("Human1")])).Reason.Value);
        Assert.Equal("hack_target_unavailable", Hack(new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.Unspawned()).Reason.Value);
    }

    [Fact]
    public void AWorshippedTerminalIsHackedOnlyFromAHostileTribe()
    {
        static string Admit(RimGovernor.Brain.GameFacts.Observations.QuestHackRisk? risk) => QuestHackPolicy.WorshippedTerminalAdmission(Ongoing("Hack_WorshippedTerminal", Objective with { HackRisk = risk })).Value;

        Assert.Equal("", Admit(new RimGovernor.Brain.GameFacts.Observations.QuestHackRisk { Hostile = true }));
        Assert.Equal("worshipped_terminal_trap", Admit(new RimGovernor.Brain.GameFacts.Observations.QuestHackRisk { Hostile = false }));
        Assert.Equal("terminal_tribe_absent", Admit(null));
    }

    [Fact]
    public void AGiftIsOwedUntilTheAskingRecipientHasEverything()
    {
        static ReviewFacts Beggars(RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk ask) => Facts(Ongoing("Beggars", Objective with
        {
            GiftRequest = new RimGovernor.Brain.GameFacts.Observations.QuestGiftRequest { Recipient = new PawnId("Beggar1"), DefName = "MedicineIndustrial", Pawns = [new PawnId("Beggar1")], Ask = ask },
        }), QuestFamily.Beggars);

        Assert.Equal("gift_not_asked", QuestCapacity.SelectQuestGift(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Away()), new MapId(0)).Reason.Value);
        Assert.Equal("gift_not_asked", QuestCapacity.SelectQuestGift(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.NotAsking()), new MapId(0)).Reason.Value);
        Assert.Equal("off_map", QuestCapacity.SelectQuestGift(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Asking(4, 3, [], [new PawnId("Human1")])), new MapId(0)).Reason.Value);

        Assert.True(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.NotAsking())));
        Assert.True(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Asking(0, 3, [], []))));
        Assert.False(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Asking(0, 0, [], []))));
    }
}
