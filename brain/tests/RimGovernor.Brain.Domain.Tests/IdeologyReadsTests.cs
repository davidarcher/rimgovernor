using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using IdeoDevelopment = RimGovernor.Brain.Policy.IdeoDevelopment;
using PawnPolicyInputs = RimGovernor.Brain.Policy.PawnPolicyInputs;
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
            Pawn("a", "Ideo_1", Known(0.8)),
            Pawn("b", "Ideo_2", Known(0.5)),
            Pawn("c", "Ideo_1", Known(0.25)),
            Pawn("d", "Ideo_1", default),
        }));

        Assert.True(ideo.BelieverList(pawns).TryGetValue(out var believers));

        Assert.Equal(["a", "c", "d"], believers.Select(b => b.Id.Value));
        Assert.True(believers[0].Certainty.TryGetValue(out var first));
        Assert.Equal(0.8, first);
        Assert.False(believers[2].Certainty.TryGetValue(out _));
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
    public void AMemeIsDesignableWhenWhatItUnlocksIsOnlyARitualNothingObliges()
    {
        // #3511: Cannibal unlocks the anytime gladiator duel; a role or an obliged ritual needing the meme has a cost the
        // design cannot value.
        var duel = new PreceptDef { defName = "GladiatorDuel", preceptClass = typeof(Precept_Ritual), requiredMemes = [Cannibal], ritualPatternBase = new RitualPatternDef { defName = "GladiatorDuel" } };
        var blinding = new PreceptDef
        {
            defName = "BlindingCeremony", preceptClass = typeof(Precept_Ritual), requiredMemes = [Pure],
            ritualPatternBase = new RitualPatternDef { defName = "BlindingCeremony", ritualObligationTriggers = [new RitualObligationTrigger_BlindingProperties()] },
        };
        var specialist = new PreceptDef { defName = "IdeoRole_PlantSpecialist", preceptClass = typeof(Precept_RoleMulti), requiredMemes = [Nudist] };
        var plain = new PreceptDef { defName = "Nudity_Male_Required", requiredMemes = [Nudist] };
        PreceptDef[] precepts = [duel, blinding, specialist, plain];

        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(Cannibal, precepts));
        Assert.True(DefinitionCatalog.UnlocksUnvaluedPrecept(Pure, precepts));
        Assert.True(DefinitionCatalog.UnlocksUnvaluedPrecept(Nudist, precepts));
        Assert.False(DefinitionCatalog.UnlocksUnvaluedPrecept(Nudist, [plain]));
    }

    private static WorkPawn Pawn(string id, string ideo, Fact<double> certainty) =>
        new()
        {
            Id = new PawnId(id),
            PolicyInputs = Known(new PawnPolicyInputs { Ideo = ideo, IdeoCertainty = certainty }),
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
