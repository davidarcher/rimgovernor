using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HumanButcherIdeologyTests
{
    private static readonly RimGovernor.Brain.GameFacts.Observations.HumanButcherCandidate Worker = new() { PawnId = "worker", CanWork = true, PawnTraits = [] };

    private static RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot Snapshot() => ColonyFactsCoreTests.Snapshot() with
    {
        ProductionBenches = [new()
        {
            Bench = ProductionBenchFixtures.Observed("bench", "ButcherTable", "token", true),
            Cooking = new RimGovernor.Brain.GameFacts.Observations.CookingProduction.NotSupported(),
            Butchering = new RimGovernor.Brain.GameFacts.Observations.ButcheringProduction.Supported { Usable = true, Recipes = [], HumanButchers = [Worker], HumanCorpseMeat = [] },
        }],
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProductionRetainsTheExactPawnIdeologyAndCandidate(bool member)
    {
        RimGovernor.Brain.GameFacts.Observations.PawnIdeo ideology = member ? new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member("other", ["Butchery_Disapproved"], null, .5) : new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None();
        var pawn = EmergencyFixtures.Placed(Worker.PawnId);
        pawn = pawn with { Settings = pawn.Settings with { PolicyInputs = pawn.Settings.PolicyInputs with { Ideo = ideology } } };
        var catalog = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

        var bench = Assert.Single(ColonyProductionReads.ColonyProductionBenches(Snapshot(), Pawns.Of(pawn), catalog));
        var candidate = Assert.Single(bench.HumanButchers);

        Assert.Same(Worker, candidate.Observed);
        Assert.Same(ideology, candidate.Ideology);
    }

    [Fact]
    public void AButcherWhosePawnRowIsAbsentFailsTheRead()
    {
        Assert.Throws<ObservationContractException>(() => ColonyProductionReads.ColonyProductionBenches(Snapshot(), Pawns.Of(), null));
    }

    [Fact]
    public void SelectionReadsTheWorkersOwnPreceptsAndExplicitNativeAbsence()
    {
        var ideology = new IdeologyMode.Installed(new Ideoligion(new IdeologyDefs
        {
            Precepts = new Dictionary<string, PreceptDef>
            {
                ["Butchery_Disapproved"] = new([new PreceptEffect
                {
                    Kind = PreceptEffectKind.SelfTookAction, HistoryEvent = HumanFoodRules.ButcheredHumanEvent, StageMoods = [-5],
                }]),
            },
        }, new IdeoligionFacts { Observed = IdeologyReadsTests.NoPrecepts }));
        var candidate = new HumanButcherCandidate { Observed = Worker, Ideology = new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member("other", ["Butchery_Disapproved"], null, .5) };

        Assert.False(HumanFoodRules.QualifyingHumanButcher([candidate], ideology).Ok);
        Assert.True(HumanFoodRules.QualifyingHumanButcher([candidate with { Ideology = new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member("other", [], null, .5) }], ideology).Ok);
        var noIdeology = candidate with { Ideology = new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None() };
        Assert.False(HumanFoodRules.QualifyingHumanButcher([noIdeology], ideology).Ok);
        Assert.True(HumanFoodRules.QualifyingHumanButcher([noIdeology], new IdeologyMode.NotInstalled()).Ok);
        var traitQualified = noIdeology with { Traits = [new PawnTrait { Observed = new() { DefName = "Cannibal" }, Effects = new TraitEffects { HumanButcher = true } }] };
        Assert.True(HumanFoodRules.QualifyingHumanButcher([traitQualified], ideology).Ok);
        Assert.False(HumanFoodRules.QualifyingHumanButcher([traitQualified with { Observed = Worker with { CanWork = false } }], ideology).Ok);
    }
}
