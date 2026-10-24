using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using IdeoDevelopment = RimGovernor.Brain.Policy.IdeoDevelopment;
using WorkPawn = RimGovernor.Brain.Policy.WorkPawn;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3490: the ideology reads goals evaluate: styles, venerated animals, development, believers and the meme gates.</summary>
public sealed class IdeologyReadsTests
{
    private static readonly MemeDef Nudist = new() { defName = "Nudism" };
    private static readonly MemeDef Cannibal = new() { defName = "Cannibalism" };
    private static readonly MemeDef Pure = new() { defName = "Purity" };

    [Fact]
    public void SnapshotRoundTripsStylesVeneratedAnimalsAndDevelopment()
    {
        var snapshot = new Obs.IdeologySnapshot
        {
            IdeoId = "Ideo_1",
            Believers = 3,
            Fluid = true,
            CanReform = true,
            DevelopmentPoints = 7,
            ReformCount = 2,
            NextReformPoints = 12,
            StyleCategories = ["Rustic", "Totemic"],
            VeneratedAnimals = ["Muffalo", "Thrumbo"],
        };

        var ideo = IdeologyDecoder.Decode(snapshot, Catalog());

        Assert.Equal(["Rustic", "Totemic"], ideo.Facts.StyleCategories);
        Assert.Equal(["Muffalo", "Thrumbo"], ideo.Facts.VeneratedAnimals);
        Assert.True(ideo.Facts.Development.TryGetValue(out var development));
        Assert.Equal(new IdeoDevelopment(true, true, 2, 7, 12), development);
    }

    [Fact]
    public void DevelopmentStaysUnknownWhenTheSectionCarriesNone()
    {
        var ideo = IdeologyDecoder.Decode(new Obs.IdeologySnapshot { IdeoId = "Ideo_1" }, Catalog());

        Assert.False(ideo.Facts.Development.TryGetValue(out _));
        Assert.Empty(ideo.Facts.StyleCategories);
    }

    [Fact]
    public void BelieverListReportsCertaintiesAndSkipsTheNonBeliever()
    {
        var ideo = IdeologyDecoder.Decode(new Obs.IdeologySnapshot { IdeoId = "Ideo_1", Believers = 3 }, Catalog());
        var pawns = Known(EquatableArray.From(new[]
        {
            Pawn("a", "Ideo_1", 0.8),
            Pawn("b", "Ideo_2", 0.5),
            Pawn("c", "Ideo_1", 0.25),
            Pawn("d", "Ideo_1", 0),
        }));

        Assert.True(ideo.BelieverList(pawns).TryGetValue(out var believers));

        Assert.Equal(["a", "c", "d"], believers.Select(b => b.Id.Value));
        Assert.Equal(0.8, believers[0].Certainty);
        Assert.Equal(0, believers[2].Certainty);
    }

    [Fact]
    public void BelieverListIsUnknownWhenAPawnRowLacksPolicyInputs()
    {
        var ideo = IdeologyDecoder.Decode(new Obs.IdeologySnapshot { IdeoId = "Ideo_1" }, Catalog());
        var unread = new WorkPawn { Id = new PawnId("a") };

        Assert.False(ideo.BelieverList(Known(EquatableArray.From(new[] { unread }))).TryGetValue(out _));
        Assert.False(ideo.BelieverList(default).TryGetValue(out _));
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

    private static WorkPawn Pawn(string id, string ideo, double certainty) =>
        new()
        {
            Id = new PawnId(id),
            PolicyInputs = Known(PawnFixtures.Inputs(ideo, certainty)),
        };

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
