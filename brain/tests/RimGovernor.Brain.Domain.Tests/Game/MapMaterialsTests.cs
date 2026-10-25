using RimGovernor.Brain.Bridge;
using P = RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3839: the planning window's map offers a material by mining its natural rock, by a stack lying on it and by
// deconstructing an unowned building, at the game's deconstruct return fraction (GenLeaving).
public sealed class MapMaterialsTests
{
    [Fact]
    public void TheWindowOffersOreLyingStacksAndWhatUnownedBuildingsYield()
    {
        var steel = new ThingDef { defName = "Steel" };
        var ore = new ThingDef { defName = "MineableSteel", building = new BuildingProperties { isNaturalRock = true, isResourceRock = true, mineableThing = steel } };
        var silver = new ThingDef { defName = "Silver" };
        var machine = new ThingDef { defName = "AncientMachine", building = new BuildingProperties(), resourcesFractionWhenDeconstructed = 0.5f };
        var vault = new ThingDef { defName = "PlayerVault", building = new BuildingProperties(), resourcesFractionWhenDeconstructed = 0.5f };
        var crate = new ThingDef { defName = "AncientCrate", building = new BuildingProperties(), resourcesFractionWhenDeconstructed = 0.5f };
        var component = new ThingDef { defName = "ComponentIndustrial" };
        DefStatTable.Cost Costs(params (string Def, long Units)[] costs) => new([.. costs.Select(c => new StatCost(c.Def, c.Units))], null);
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
            {
                // Half of 50 Steel is 25; half of one component rounds to nothing for certain.
                [(machine, null)] = Costs(("Steel", 50), ("ComponentIndustrial", 1)),
                [(vault, null)] = Costs(("Silver", 100)),
            },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        var catalog = new DefinitionCatalog([steel, ore, silver, machine, vault, crate, component, new TerrainDef { defName = "Soil" }], stats, new CatalogDerived("Silver", "Wort", 10f));
        P.Thing Building(string def, P.ThingFaction faction) => new()
        {
            Def = def, Category = P.ThingCategory.Building, Faction = faction, Flags = P.ThingMarks.Edifice | P.ThingMarks.Deconstructible, Building = new(),
        };
        P.SiteCell Cell(int x, params P.Thing[] things) => SiteCellFixtures.At(new(x, 0)) with { Things = things };

        var sources = catalog.RingStuffSourcesOf(
        [
            Cell(0, new P.Thing { Def = "MineableSteel", Category = P.ThingCategory.Building, Flags = P.ThingMarks.Edifice | P.ThingMarks.NaturalRock, Building = new() }),
            Cell(1, new P.Thing { Def = "Silver", Category = P.ThingCategory.Item, Count = 30 }),
            Cell(2, Building("AncientMachine", P.ThingFaction.None)),
            Cell(3, Building("PlayerVault", P.ThingFaction.Player)),
        ]);
        Assert.Equal(["Steel"], sources.Mined);
        Assert.Equal(["Silver"], sources.Items);
        Assert.Equal(["Steel"], sources.Salvage);
        Assert.Empty(sources.Unsalvaged);
        Assert.True(sources.Offers("Steel"));
        Assert.False(sources.Offers("ComponentIndustrial"));

        // A building the catalog has no cost row for leaves its salvage unread.
        Assert.Equal(["AncientCrate"], catalog.RingStuffSourcesOf([Cell(0, Building("AncientCrate", P.ThingFaction.None))]).Unsalvaged);
    }
}
