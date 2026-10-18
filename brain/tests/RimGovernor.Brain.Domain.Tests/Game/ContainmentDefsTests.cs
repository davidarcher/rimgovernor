using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3435: the containment inputs read every terrain's stat; a terrain that does not show it adds no floor, and a
// second shell's inputs keep the def-only part while reading their own wall and door.
public sealed class ContainmentDefsTests
{
    private static readonly ThingDef Silver = new() { defName = "Silver" };
    private static readonly ThingDef Gold = new() { defName = "Gold" };
    private static readonly ThingDef Wall = new() { defName = "Wall" };
    private static readonly ThingDef Platform = new() { defName = "Platform", comps = [new CompProperties_EntityHolderPlatform { containmentFactor = 2f }] };
    private static readonly TerrainDef Soil = new() { defName = "Soil" };
    private static readonly TerrainDef Plate = new() { defName = "Plate" };

    [Fact]
    public void FloorIsTheTerrainShowingTheStrongestStatAndEachShellReadsItsOwnWall()
    {
        var catalog = Catalog();

        var silver = catalog.ContainmentDefs("Wall", "Silver", "Wall", "Silver");
        var gold = catalog.ContainmentDefs("Wall", "Gold", "Wall", "Silver");

        Assert.Equal("Platform", silver.Holder);
        Assert.Equal("Plate", silver.Floor.Def);
        Assert.Equal(30, silver.Floor.Strength);
        Assert.Equal((100, 100), (silver.WallHP, silver.DoorHP));
        Assert.Equal((250, 100), (gold.WallHP, gold.DoorHP));
        Assert.Equal(silver.Floor, gold.Floor);
    }

    private static DefinitionCatalog Catalog()
    {
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
            {
                [(Wall, Silver)] = new([], null),
                [(Wall, Gold)] = new([], null),
                [(Soil, null)] = new([], null),
                [(Plate, null)] = new([], null),
            },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(Wall, Silver, CatalogStats.MaxHitPoints)] = new(100f, true, null),
                [(Wall, Gold, CatalogStats.MaxHitPoints)] = new(250f, true, null),
                [(Soil, null, CatalogStats.ContainmentStrength)] = new(0f, false, null),
                [(Plate, null, CatalogStats.ContainmentStrength)] = new(30f, true, null),
            },
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        return new([Silver, Gold, Wall, Platform, Soil, Plate, new StatDef { defName = "ContainmentStrength" }], table, new CatalogDerived("Silver", "Wort", 10f));
    }
}
