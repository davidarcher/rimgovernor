using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class ButcherTablePlanningTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void FirstReviewReadsTheButcherTableAndTreatsKnownInsufficientStockAsUnaffordable(long stock, bool affordable)
    {
        Assert.Contains(KitchenSeparationRules.ButcherTableDefinition, DefinitionsReads.StarterDefinitions);
        var wood = new ThingDef { defName = nameof(ThingDefOf.WoodLog), stuffProps = new StuffProperties { commonality = 1 } };
        var table = new ThingDef
        {
            defName = KitchenSeparationRules.ButcherTableDefinition, thingClass = typeof(Building_WorkTable),
            designationCategory = new DesignationCategoryDef(), stuffCategories = [new StuffCategoryDef()],
        };
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
            {
                [(table, wood)] = new([new(wood.defName, 50)], null), [(wood, null)] = new([], null),
            },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(table, wood, CatalogStats.WorkToBuild)] = new(100, true, null),
                [(wood, null, CatalogStats.MarketValue)] = new(1, true, null),
            }, new Dictionary<BuildableDef, IReadOnlyList<string>> { [table] = [wood.defName] }, new HashSet<CatalogStat>(CatalogStats.All));
        var catalog = new DefinitionCatalog([wood, table, new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }],
            stats, new CatalogDerived("Silver", "Wort", 10f));
        var definition = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(), Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }.Resolve(table.defName);
        var colony = ColonyFactsCoreTests.Reading();
        colony = colony with { Facts = colony.Facts with
        {
            Definitions = [definition], Census = colony.Snapshot with { Resources = [new() { DefName = wood.defName, Units = stock }] },
        } };

        Assert.Equal(affordable, FacilityPlanningRules.ButcherTableAffordable(colony.Facts));
    }
}
