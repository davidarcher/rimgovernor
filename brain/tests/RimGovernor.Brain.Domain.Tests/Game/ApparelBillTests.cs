using RimGovernor.Brain.Bridge;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3435: a gear census asks the bill of every option; the recipe making a def alone is the first by defName.
public sealed class ApparelBillTests
{
    private static readonly ThingDef Cloth = new() { defName = "Cloth" };
    private static readonly ThingDef Parka = new() { defName = "Parka" };
    private static readonly ThingDef Hat = new() { defName = "Hat" };

    [Fact]
    public void BillIsTheFirstRecipeByNameThatMakesTheDefAlone()
    {
        var catalog = Catalog(
            Recipe("B_MakeParka", "Tailoring", Parka),
            Recipe("A_MakeParka", "ComplexClothing", Parka),
            Recipe("C_MakeBoth", "Hats", Parka, Hat));

        Assert.Equal(["ComplexClothing"], catalog.ApparelBill("Parka", "Cloth")!.Value.Research);
        Assert.Equal([new StatCost("Cloth", 80)], catalog.ApparelBill("Parka", "Cloth")!.Value.Ingredients);
        Assert.Null(catalog.ApparelBill("Hat", "Cloth"));
    }

    private static RecipeDef Recipe(string name, string research, params ThingDef[] products) => new()
    {
        defName = name,
        researchPrerequisite = new ResearchProjectDef { defName = research },
        products = [.. products.Select(p => new ThingDefCountClass(p, 1))],
    };

    private static DefinitionCatalog Catalog(params RecipeDef[] recipes)
    {
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(Parka, Cloth)] = new([new StatCost("Cloth", 80)], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        return new([Cloth, Parka, Hat, new TerrainDef { defName = "Soil" }, .. recipes], table, new CatalogDerived("Cloth", "Wort", 10f));
    }
}
