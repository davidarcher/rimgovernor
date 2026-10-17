using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3147: the catalog reads stats from the load's precomputed table keyed by Def objects, with no game hop. A shown
// stat is its value, a hidden one reads as not shown, a hidden read of a stat the table does not compute hidden is a
// declaration error, and a subject outside the table is a StatException rather than a default.
public sealed class DefStatTableTests
{
    private static readonly ThingDef Silver = new() { defName = "Silver" };
    private static readonly ThingDef Wall = new() { defName = "Wall" };
    private static readonly TerrainDef Soil = new() { defName = "Soil" };

    [Fact]
    public void ShownStatIsTheTableValue()
    {
        var catalog = Catalog();

        Assert.Equal(12f, catalog.StatValue("Wall", "Silver", CatalogStats.MarketValue));
        Assert.Equal((0f, false), catalog.ShownStatValue("Wall", "Silver", CatalogStats.Comfort));
        Assert.Equal(0.5f, catalog.PlannerStatValue("Wall", "Silver", CatalogStats.Flammability));
    }

    [Fact]
    public void HiddenReadOfAStatNotComputedHiddenIsADeclarationError()
    {
        var catalog = Catalog();

        Assert.Throws<CatalogException>(() => catalog.PlannerStatValue("Wall", "Silver", CatalogStats.Comfort));
    }

    [Fact]
    public void SubjectOutsideTheTableIsAStatException()
    {
        var catalog = Catalog();

        Assert.Throws<StatException>(() => catalog.ShownStatValue("Wall", string.Empty, CatalogStats.MarketValue));
        Assert.Throws<StatException>(() => catalog.ShownStatValue("Wall", "Gold", CatalogStats.MarketValue));
    }

    [Fact]
    public void StuffAndCostRowsComeFromTheTable()
    {
        var catalog = Catalog();

        Assert.Equal(["Silver"], catalog.AllowedStuffs("Wall"));
        Assert.Equal([new StatCost("Silver", 5)], catalog.AdjustedCosts("Wall", "Silver"));
        Assert.Empty(catalog.TerrainAdjustedCosts("Soil"));
        Assert.Throws<StatException>(() => catalog.AdjustedCosts("Silver", string.Empty));
    }

    private static DefinitionCatalog Catalog()
    {
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
            {
                [(Wall, Silver)] = new([new StatCost("Silver", 5)], null),
                [(Silver, null)] = new(null, "Silver has no cost row"),
                [(Soil, null)] = new([], null),
            },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(Wall, Silver, CatalogStats.MarketValue)] = new(12f, true, null),
                [(Wall, Silver, CatalogStats.Flammability)] = new(0.5f, false, null),
            },
            new Dictionary<BuildableDef, IReadOnlyList<string>> { [Wall] = ["Silver"], [Silver] = [], [Soil] = [] },
            new HashSet<CatalogStat>(CatalogStats.All));
        return new([Silver, Wall, Soil], table, new CatalogDerived("Silver", "Wort", 10f));
    }
}
