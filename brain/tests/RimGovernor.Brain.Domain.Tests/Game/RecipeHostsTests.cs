using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3435: the recipes making a def are read off an index built once per catalog, in defName order.
public sealed class RecipeHostsTests
{
    private static readonly ThingDef Parka = new() { defName = "Parka" };
    private static readonly ThingDef Hat = new() { defName = "Hat" };
    private static readonly ThingDef Bench = new()
    {
        defName = "TailorBench", category = ThingCategory.Building, thingClass = typeof(Building_WorkTable), designationCategory = new DesignationCategoryDef { defName = "Production" },
    };

    [Fact]
    public void HostsAreTheRecipesMakingTheProductByNameWithTheirOwnProducts()
    {
        var catalog = new DefinitionCatalog(
            [new ThingDef { defName = "Cloth" }, Parka, Hat, Bench, new TerrainDef { defName = "Soil" },
                Recipe("C_MakeBoth", Parka, Hat, Parka), Recipe("A_MakeParka", Parka), Recipe("B_MakeHat", Hat)],
            new DefStatTable(
                new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>(),
                new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
                new Dictionary<BuildableDef, IReadOnlyList<string>>(),
                new HashSet<CatalogStat>(CatalogStats.All)),
            new CatalogDerived("Cloth", "Wort", 10f));

        var parka = catalog.RecipeHosts("Parka", null);
        Assert.Equal(["A_MakeParka", "C_MakeBoth"], parka.Select(h => h.Definition));
        Assert.Equal(["Parka", "Hat"], parka[1].Products.Select(p => p.Value));
        Assert.Equal(["TailorBench"], parka[0].Benches);
        Assert.Equal(["B_MakeHat", "C_MakeBoth"], catalog.RecipeHosts("Hat", null).Select(h => h.Definition));
        Assert.Empty(catalog.RecipeHosts("Bench", null));
    }

    private static RecipeDef Recipe(string name, params ThingDef[] products) => new()
    {
        defName = name,
        recipeUsers = [Bench],
        products = [.. products.Select(p => new ThingDefCountClass(p, 1))],
    };
}
