using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire;
using IdeoDevelopment = RimGovernor.Brain.Policy.IdeoDevelopment;
using RimWorld;
using Verse;
using Xunit;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3490: the ideology reads goals evaluate: styles, venerated animals, development, believers and the meme gates.</summary>
public sealed class IdeologyReadsTests
{
    private static readonly MemeDef Nudist = new() { defName = "Nudism" };
    private static readonly MemeDef Cannibal = new() { defName = "Cannibalism" };
    private static readonly MemeDef Pure = new() { defName = "Purity" };

    /// <summary>A fixed ideoligion holding nothing: the shared whole section the tests build theirs from.</summary>
    internal static readonly Obs.IdeologySnapshot NoPrecepts = new()
    {
        IdeoId = "Ideo_1",
        Memes = [],
        Precepts = [],
        Roles = [],
        Rituals = [],
        Buildings = [],
        Believers = 0,
        BelieverPawns = [],
        Development = new Obs.IdeoDevelopment.Fixed(),
        StyleCategories = [],
        VeneratedAnimals = [],
    };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var snapshot = NoPrecepts with
        {
            Memes = ["Nudism"],
            Precepts = [new Obs.IdeoPrecept { Id = "Precept_1", DefName = "Nudity_Male_Required" }],
            Roles = [new Obs.IdeoRole { Id = "Precept_2", DefName = "IdeoRole_Leader", Active = true, Pawns = [new PawnId("Human7")], Converts = false, ConvertReady = false }],
            Rituals = [new Obs.IdeoRitual { Id = "Precept_3", Pattern = "Gathering", LastFinishedTick = -1, ActiveObligations = 0, RepeatPenaltyActive = false, Running = false }],
            Buildings =
            [
                new Obs.IdeoBuilding
                {
                    Building = "Altar", Applies = true, Present = false, Satisfied = false, MinArea = 25, MinImpressiveness = 0, FloorTags = [],
                    Things = [new("Column", 2)], ForbiddenBuildingTags = [], Unread = [],
                },
            ],
            Believers = 1,
            BelieverPawns = [new PawnId("Human7")],
            Development = new Obs.IdeoDevelopment.Fluid(true, 7, 2, 12),
        };

        var json = WireJson.Serialize(snapshot);

        Assert.Equal(
            """{"ideoId":"Ideo_1","memes":["Nudism"],"precepts":[{"id":"Precept_1","defName":"Nudity_Male_Required"}],"roles":[{"id":"Precept_2","defName":"IdeoRole_Leader","active":true,"pawns":["Human7"],"converts":false,"convertReady":false}],"rituals":[{"id":"Precept_3","pattern":"Gathering","lastFinishedTick":-1,"activeObligations":0,"repeatPenaltyActive":false,"running":false}],"buildings":[{"building":"Altar","applies":true,"present":false,"satisfied":false,"minArea":25,"minImpressiveness":0,"floorTags":[],"things":[{"def":"Column","count":2}],"forbiddenBuildingTags":[],"unread":[]}],"believers":1,"believerPawns":["Human7"],"development":{"type":"fluid","canReform":true,"points":7,"reformCount":2,"nextReformPoints":12},"styleCategories":[],"veneratedAnimals":[]}""",
            json);
        Assert.Equal(snapshot, WireJson.Deserialize<Obs.IdeologySnapshot>(json));
        Assert.Equal(NoPrecepts, WireJson.Deserialize<Obs.IdeologySnapshot>(WireJson.Serialize(NoPrecepts)));
    }

    [Fact]
    public void SnapshotRoundTripsStylesVeneratedAnimalsAndDevelopment()
    {
        var snapshot = NoPrecepts with
        {
            Believers = 3,
            Development = new Obs.IdeoDevelopment.Fluid(true, 7, 2, 12),
            StyleCategories = ["Rustic", "Totemic"],
            VeneratedAnimals = ["Muffalo", "Thrumbo"],
        };

        var ideo = IdeologyDecoder.Decode(snapshot, Catalog());

        Assert.Equal(["Rustic", "Totemic"], ideo.Facts.StyleCategories);
        Assert.Equal(["Muffalo", "Thrumbo"], ideo.Facts.VeneratedAnimals);
        Assert.Equal(new IdeoDevelopment(true, true, 2, 7, 12), ideo.Facts.Development);
    }

    [Fact]
    public void AFixedIdeoligionNeverReforms()
    {
        var ideo = IdeologyDecoder.Decode(NoPrecepts, Catalog());

        Assert.Equal(IdeoDevelopment.Fixed, ideo.Facts.Development);
        Assert.Empty(ideo.Facts.StyleCategories);
    }

    [Fact]
    public void TheFramesIdeologyModeIsNotInstalledWithoutTheExpansionAndInstalledWithItsPrimaryIdeoligion()
    {
        Assert.IsType<RimGovernor.Brain.Policy.IdeologyMode.NotInstalled>(IdeologyDecoder.Mode(false, null, Catalog()));

        var installed = Assert.IsType<RimGovernor.Brain.Policy.IdeologyMode.Installed>(IdeologyDecoder.Mode(true, NoPrecepts with { IdeoId = "Ideo_7" }, Catalog()));
        Assert.Equal("Ideo_7", installed.Primary.Facts.IdeoId);
    }

    [Fact]
    public void TheFramesIdeologyModeRefusesAnAbsentFlagAndAFlagThatContradictsItsSection()
    {
        Assert.Contains("ideology flag", Assert.Throws<RimGovernor.Brain.Bridge.Contracts.BridgeContractException>(() => IdeologyDecoder.Mode(null, null, Catalog())).Message);
        Assert.Contains("ideology flag", Assert.Throws<RimGovernor.Brain.Bridge.Contracts.BridgeContractException>(() => IdeologyDecoder.Mode(null, NoPrecepts, Catalog())).Message);
        Assert.Contains("primary ideoligion", Assert.Throws<RimGovernor.Brain.Bridge.Contracts.BridgeContractException>(() => IdeologyDecoder.Mode(true, null, Catalog())).Message);
        Assert.Contains("ideology_active false", Assert.Throws<RimGovernor.Brain.Bridge.Contracts.BridgeContractException>(() => IdeologyDecoder.Mode(false, NoPrecepts, Catalog())).Message);
    }

    [Fact]
    public void CatalogListsMemePreceptsAndTheMemesGatingAnIssue()
    {
        var defs = Catalog().IdeologyDefs()!;

        Assert.Equal([["Cannibal_Preferred"]], defs.Memes["Cannibalism"].RequireOne);
        Assert.Equal(["Nudity_Male_Never"], defs.Memes["Purity"].ForbiddenPrecepts);
        var gate = defs.Gate(["Nudity_Male", "Cannibalism"]);
        Assert.Equal(["Cannibalism", "Nudism"], gate.Required);
        Assert.Equal(["Purity"], gate.Conflicting);
    }

    [Fact]
    public void AMemeIsDesignableWhenWhatItUnlocksIsARoleOrARitualNothingObliges()
    {
        // #3511: Cannibal unlocks the anytime gladiator duel. #3602: Rancher and a leader meme unlock roles, which skill-fit
        // assignment fills. An obliged ritual or a building precept needing the meme has a cost the design cannot value.
        // IdeoligionOptions marks a meme Supported only when this rule passes; it is tested here because the options also
        // need core-content packs and the venerated-animal DefOf, which a unit test cannot build.
        MemeDef rancher = new() { defName = "Rancher" }, leader = new() { defName = "Leader" }, shrine = new() { defName = "Shrine" };
        PreceptDef[] precepts =
        [
            new() { defName = "GladiatorDuel", preceptClass = typeof(Precept_Ritual), requiredMemes = [Cannibal], ritualPatternBase = new RitualPatternDef { defName = "GladiatorDuel" } },
            new() { defName = "IdeoRole_AnimalsSpecialist", preceptClass = typeof(Precept_RoleMulti), requiredMemes = [rancher] },
            new() { defName = "IdeoRole_Leader", preceptClass = typeof(Precept_RoleSingle), requiredMemes = [leader] },
            new()
            {
                defName = "BlindingCeremony", preceptClass = typeof(Precept_Ritual), requiredMemes = [Pure],
                ritualPatternBase = new RitualPatternDef { defName = "BlindingCeremony", ritualObligationTriggers = [new RitualObligationTrigger_BlindingProperties()] },
            },
            new() { defName = "Altar", preceptClass = typeof(Precept_Building), requiredMemes = [shrine] },
            new() { defName = "Nudity_Male_Required", requiredMemes = [Nudist] },
        ];

        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(Cannibal, precepts));
        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(rancher, precepts));
        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(leader, precepts));
        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(Nudist, precepts));
        Assert.True(DefinitionCatalog.UnlocksUnvaluedPrecept(Pure, precepts));
        Assert.True(DefinitionCatalog.UnlocksUnvaluedPrecept(shrine, precepts));
    }

    private static DefinitionCatalog Catalog()
    {
        var nudityIssue = new IssueDef { defName = "Nudity_Male" };
        var cannibalIssue = new IssueDef { defName = "Cannibalism" };
        var cannibalPreferred = new PreceptDef { defName = "Cannibal_Preferred", issue = cannibalIssue, requiredMemes = [Cannibal] };
        var nudityRequired = new PreceptDef { defName = "Nudity_Male_Required", issue = nudityIssue, requiredMemes = [Nudist] };
        var nudityNever = new PreceptDef { defName = "Nudity_Male_Never", issue = nudityIssue, conflictingMemes = [Pure] };
        Cannibal.requireOne = [[cannibalPreferred]];
        return new DefinitionCatalog(
            [
                new ThingDef { defName = "Silver" },
                new TerrainDef { defName = "Soil" },
                Nudist, Cannibal, Pure, nudityIssue, cannibalIssue, cannibalPreferred, nudityRequired, nudityNever,
            ],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
    }
}
