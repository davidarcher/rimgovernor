using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3848: what an Anomaly project unlocks comes from the production reads. The catalog gathers a project's unlocks the way
// ResearchProjectDef.UnlockedDefs does, and the gear read offers the bills only unfinished knowledge projects keep from the colony.
// Through the research census and the model input, a fresh colony prices a knowledge point by the garment it unlocks.
public sealed class KnowledgeUnlockProductionTests
{
    private static readonly ThingDef Cloth = new() { defName = KnowledgeValueTests.ClothDef };

    // The census's locked bill for the unlocked duster: a fresh colony's skill-6 crafter could make it (#3850).
    private static readonly O.GearLockedBill[] Locked =
        [new() { DefName = KnowledgeValueTests.Unlocked, Craftable = true, Crafters = [new(6, false, 0)] }];

    private static DefinitionCatalog Catalog()
    {
        var basic = new KnowledgeCategoryDef { defName = KnowledgeValueTests.Basic };
        var advanced = new KnowledgeCategoryDef { defName = KnowledgeValueTests.Advanced, overflowCategory = basic };
        var project = new ResearchProjectDef { defName = KnowledgeValueTests.Bioferrite, knowledgeCategory = basic, knowledgeCost = 4 };
        var duster = new ThingDef
        {
            defName = KnowledgeValueTests.Unlocked,
            apparel = new ApparelProperties { bodyPartGroups = [new BodyPartGroupDef { defName = "Torso" }], layers = [new ApparelLayerDef { defName = "Shell" }] },
        };
        var platform = new ThingDef { defName = KnowledgeValueTests.Platform, category = Verse.ThingCategory.Building, researchPrerequisites = [project] };
        var filter = new ThingFilter();
        filter.SetAllow(Cloth, true);
        var recipe = new RecipeDef { defName = "Make_" + KnowledgeValueTests.Unlocked, products = [new ThingDefCountClass(duster, 1)], researchPrerequisite = project, fixedIngredientFilter = filter };
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
        {
            [(duster, Cloth, CatalogStats.ArmorSharp)] = new(0.8f, true, null), [(duster, Cloth, CatalogStats.ArmorBlunt)] = new(0.1f, true, null),
            [(duster, Cloth, CatalogStats.InsulationCold)] = new(10f, true, null), [(duster, Cloth, CatalogStats.InsulationHeat)] = new(5f, true, null),
        };
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(duster, Cloth)] = new([new StatCost(KnowledgeValueTests.ClothDef, 80)], null) },
            values, new Dictionary<BuildableDef, IReadOnlyList<string>> { [duster] = [KnowledgeValueTests.ClothDef] }, new HashSet<CatalogStat>(CatalogStats.All));
        return new([Cloth, duster, platform, basic, advanced, project, recipe, new TerrainDef { defName = "Soil" }], table, new CatalogDerived("Cloth", "Wort", 10f));
    }

    [Fact]
    public void TheCatalogGathersAProjectsUnlocksAndTheGearReadOffersTheBillsItKeepsFromTheColony()
    {
        var catalog = Catalog();

        Assert.Equal([KnowledgeValueTests.Unlocked, KnowledgeValueTests.Platform], catalog.Research[KnowledgeValueTests.Bioferrite].Unlocks);
        var bill = Assert.Single(ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal), Locked));
        Assert.Equal((KnowledgeValueTests.Unlocked, KnowledgeValueTests.ClothDef, GearSource.Bill, GearSlot.Outer), (bill.Definition.Value, bill.Stuff.Value, bill.Source, bill.Slot));
        Assert.Equal([KnowledgeValueTests.Bioferrite], bill.Research);
        Assert.Equal(0.8, bill.Sharp, 6);
        Assert.Equal(CraftedQualityRules.Odds(new(6, false, 0)), bill.QualityOdds);
        // A bill no colonist could craft is no option; a census without the def's row is an unread fact.
        Assert.Empty(ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal), [Locked[0] with { Craftable = false, Crafters = [] }]));
        Assert.Throws<RequiredFactException>(() => ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal), []));
        // Once the project is finished the census lists the bill itself.
        Assert.Empty(ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal) { KnowledgeValueTests.Bioferrite }, Locked));
    }

    [Fact]
    public void AFreshAnomalyColonyPricesAKnowledgePointByTheGarmentItsProjectUnlocks()
    {
        var catalog = Catalog();
        var bills = ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal), Locked);

        var marginals = KnowledgeValueTests.Priced(KnowledgeValueTests.AnomalyRead(), catalog.Research, catalog.KnowledgeOverflow, bills);

        Assert.True(marginals.PerUnit[ProjectGraph.KnowledgeKey(KnowledgeValueTests.Basic)] > 0, string.Join(',', marginals.Findings));
        Assert.Contains($"{ProjectGraph.KnowledgeUnlockUnmodelled}:{KnowledgeValueTests.Bioferrite}:{KnowledgeValueTests.Platform}", marginals.Findings);
        Assert.DoesNotContain($"{ProjectGraph.KnowledgeUnlockUnmodelled}:{KnowledgeValueTests.Bioferrite}:{KnowledgeValueTests.Unlocked}", marginals.Findings);
    }

    [Fact]
    public void BeforeTheCodexShowsTheProjectAFreshColonyPricesAPointAtZeroAndThrowsNothing()
    {
        // A new colony has discovered no entity, so the codex hides every Anomaly project and no slot funds one.
        var catalog = Catalog();
        var marginals = KnowledgeValueTests.Priced(KnowledgeValueTests.AnomalyRead(ResearchRules.ResearchLockHidden), catalog.Research, catalog.KnowledgeOverflow,
            ColonyGearReads.KnowledgeBillOptions(catalog, new HashSet<string>(StringComparer.Ordinal), Locked));

        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(KnowledgeValueTests.Basic)]);
        Assert.DoesNotContain(marginals.Findings, f => f.StartsWith(ProjectGraph.KnowledgeUnlockUnmodelled, StringComparison.Ordinal));
    }
}
