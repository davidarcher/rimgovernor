using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3864: the quest monument, hack target, hack risk and gift request rows are total records whose lifecycle is a
// named state, so the policy reads the state instead of a nullable field.
public sealed class QuestMonumentHackGiftTests
{
    private static Pb.QuestMonumentPiece Piece(Pb.QuestMonumentPieceSite site) => new()
    {
        DefName = "Wall", Offset = new Cell(1, 0), Rotation = Rotation.East, Footprint = [new Cell(0, 0)], Site = site,
        Options = [new Pb.QuestMonumentBuildOption { Stuff = "Steel", Costs = [new Pb.QuestMonumentMaterial("Steel", 5)], Work = 135 }],
    };

    private static Pb.QuestMonument Monument(Pb.QuestMonumentState state, Pb.QuestMonumentPieceSite site, params Pb.QuestMonumentResource[] resources) => new()
    {
        MarkerId = "MonumentMarker1", DefName = "MonumentMarker", MapId = 0, State = state, Pieces = [Piece(site)],
        Resources = [.. resources], AvailableResources = [new Pb.QuestMonumentMaterial("Steel", 40)],
    };

    private static readonly Pb.QuestMonumentState.Installed Standing = new(new Cell(10, 10), false, null);

    private static readonly Pb.QuestMonumentPieceSite.Placed Open = new(false, false, true);

    private static JoinerOffer Ongoing(string script, QuestFamily family, QuestObjective objective) => new()
    {
        Quest = new("q1"), ScriptDef = script, State = "Ongoing", Objectives = [objective],
        Profile = Known(new QuestProfile { Family = family }),
    };

    private static RoundsFacts Facts(JoinerOffer offer) => new() { QuestOffers = Known<EquatableArray<JoinerOffer>>([offer]) };

    private static QuestMonumentMethod Select(Pb.QuestMonument marker) => QuestMonumentPolicy.SelectQuestMonument(
        Known<EquatableArray<JoinerOffer>>([Ongoing("BuildMonument", QuestFamily.BuildMonument, new QuestObjective { Monument = marker })]),
        new MapId(0), new Dictionary<Resource, long> { [new Resource("Steel")] = 100 }, []);

    [Fact]
    public void EveryMonumentStateRoundTripsExactly()
    {
        Pb.QuestMonumentState[] states =
        [
            new Pb.QuestMonumentState.Offered([new Cell(3, 3)], [new Pb.QuestMonumentMaterial("Steel", 50)]),
            new Pb.QuestMonumentState.Packed([new Cell(3, 3)]),
            new Pb.QuestMonumentState.Installed(new Cell(10, 10), true, "Wall9"),
        ];
        foreach (var state in states)
        {
            var marker = Monument(state, state is Pb.QuestMonumentState.Installed ? Open : new Pb.QuestMonumentPieceSite.Unplaced(),
                new Pb.QuestMonumentResource { Id = "Steel1", DefName = "Steel", Cell = new Cell(4, 4), EligibleHaulers = [new PawnId("Human1")] });
            var json = WireJson.Serialize(marker);
            var back = WireJson.Deserialize<Pb.QuestMonument>(json);
            Assert.Equal(json, WireJson.Serialize(back));
            Assert.Equal(marker, back);
        }
    }

    [Fact]
    public void HackAndGiftRowsRoundTripExactly()
    {
        Pb.QuestHackTarget[] targets =
        [
            new() { Id = "Terminal1", LockedOut = false, Autohack = false, Satisfied = false, Place = new Pb.QuestHackTargetPlace.OnMap(0, [new PawnId("Human1")]) },
            new() { Id = "Terminal2", LockedOut = true, Autohack = false, Satisfied = false, Place = new Pb.QuestHackTargetPlace.Unspawned() },
        ];
        foreach (var target in targets)
        {
            var json = WireJson.Serialize(target);
            Assert.Equal(target, WireJson.Deserialize<Pb.QuestHackTarget>(json));
        }
        Pb.QuestGiftAsk[] asks =
        [
            new Pb.QuestGiftAsk.Away(),
            new Pb.QuestGiftAsk.NotAsking(),
            new Pb.QuestGiftAsk.Asking(0, 20, [new PawnId("Human2")], [new PawnId("Human1")]),
        ];
        foreach (var ask in asks)
        {
            var gift = new Pb.QuestGiftRequest { Recipient = new PawnId("Beggar1"), DefName = "MedicineIndustrial", Pawns = [new PawnId("Beggar1")], Ask = ask };
            var json = WireJson.Serialize(gift);
            Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<Pb.QuestGiftRequest>(json)));
            Assert.Equal(gift, WireJson.Deserialize<Pb.QuestGiftRequest>(json));
        }
        var risk = new Pb.QuestHackRisk { Hostile = true };
        Assert.Equal(risk, WireJson.Deserialize<Pb.QuestHackRisk>(WireJson.Serialize(risk)));
    }

    [Fact]
    public void TheMonumentPlanFollowsTheMarkerState()
    {
        // A packed marker installs at its first unprotected legal cell; an offered one still waits in its drop pods.
        var packed = Select(Monument(new Pb.QuestMonumentState.Packed([new Cell(3, 3)]), new Pb.QuestMonumentPieceSite.Unplaced()));
        Assert.Equal(("install", new Cell(3, 3)), (packed.Kind, packed.Move!.Cell));
        Assert.Equal("marker_unavailable", Select(Monument(new Pb.QuestMonumentState.Offered([new Cell(3, 3)], []), new Pb.QuestMonumentPieceSite.Unplaced())).Reason);

        // An installed marker hauls loose material first, then builds its open piece at the marker offset.
        var loose = new Pb.QuestMonumentResource { Id = "Steel1", DefName = "Steel", Cell = new Cell(4, 4), EligibleHaulers = [new PawnId("Human2"), new PawnId("Human1")] };
        var haul = Select(Monument(Standing, Open, loose));
        Assert.Equal(("haul", new PawnId("Human1")), (haul.Kind, haul.Haul!.Pawn));
        var build = Select(Monument(Standing, Open));
        Assert.Equal(("build", new Cell(11, 10), Rotation.East, "Steel"), (build.Kind, build.Build!.Cell, build.Build.Rotation, build.Build.Stuff));

        // A complete marker is protected, a disallowed building blocks it, and an unplaced piece on an installed marker is a broken read.
        Assert.Equal("protect", Select(Monument(Standing with { AllDone = true }, Open)).Kind);
        Assert.Equal("disallowed_building", Select(Monument(Standing with { DisallowedBuildingId = "Wall9" }, Open)).Reason);
        Assert.Throws<RequiredFactException>(() => Select(Monument(Standing, new Pb.QuestMonumentPieceSite.Unplaced())));
        Assert.Equal("wait", Select(Monument(Standing, new Pb.QuestMonumentPieceSite.Placed(true, false, true))).Kind);
    }

    [Fact]
    public void AHackTargetIsDesignatedOnlyWhenItStandsOnThisMap()
    {
        static QuestHackWork Hack(Pb.QuestHackTargetPlace place) => QuestHackPolicy.SelectQuestHack(Facts(Ongoing("Hack_Spacedrone", QuestFamily.Hack, new QuestObjective
        {
            HackTargets = [new Pb.QuestHackTarget { Id = "Terminal1", LockedOut = false, Autohack = false, Satisfied = false, Place = place }],
        })), new MapId(0));

        Assert.Equal("Terminal1", Hack(new Pb.QuestHackTargetPlace.OnMap(0, [new PawnId("Human1")])).Hack!.Target);
        Assert.Equal("hacker_unavailable", Hack(new Pb.QuestHackTargetPlace.OnMap(0, [])).Reason.Value);
        Assert.Equal("off_map", Hack(new Pb.QuestHackTargetPlace.OnMap(4, [new PawnId("Human1")])).Reason.Value);
        Assert.Equal("hack_target_unavailable", Hack(new Pb.QuestHackTargetPlace.Unspawned()).Reason.Value);
    }

    [Fact]
    public void AWorshippedTerminalIsHackedOnlyFromAHostileTribe()
    {
        static string Admit(Pb.QuestHackRisk? risk) => QuestHackPolicy.WorshippedTerminalAdmission(Ongoing("Hack_WorshippedTerminal", QuestFamily.Hack, new QuestObjective { HackRisk = risk })).Value;

        Assert.Equal("", Admit(new Pb.QuestHackRisk { Hostile = true }));
        Assert.Equal("worshipped_terminal_trap", Admit(new Pb.QuestHackRisk { Hostile = false }));
        Assert.Equal("terminal_tribe_absent", Admit(null));
    }

    [Fact]
    public void AGiftIsOwedUntilTheAskingRecipientHasEverything()
    {
        static RoundsFacts Beggars(Pb.QuestGiftAsk ask) => Facts(Ongoing("Beggars", QuestFamily.Beggars, new QuestObjective
        {
            Gift = new Pb.QuestGiftRequest { Recipient = new PawnId("Beggar1"), DefName = "MedicineIndustrial", Pawns = [new PawnId("Beggar1")], Ask = ask },
        }));

        Assert.Equal("gift_not_asked", QuestCapacity.SelectQuestGift(Beggars(new Pb.QuestGiftAsk.Away()), new MapId(0)).Reason.Value);
        Assert.Equal("gift_not_asked", QuestCapacity.SelectQuestGift(Beggars(new Pb.QuestGiftAsk.NotAsking()), new MapId(0)).Reason.Value);
        Assert.Equal("off_map", QuestCapacity.SelectQuestGift(Beggars(new Pb.QuestGiftAsk.Asking(4, 3, [], [new PawnId("Human1")])), new MapId(0)).Reason.Value);

        Assert.True(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new Pb.QuestGiftAsk.NotAsking())));
        Assert.True(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new Pb.QuestGiftAsk.Asking(0, 3, [], []))));
        Assert.False(QuestHackPolicy.IdeologyWorkDeficit(Beggars(new Pb.QuestGiftAsk.Asking(0, 0, [], []))));
    }
}
