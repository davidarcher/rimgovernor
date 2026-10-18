using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3523: a sowable plant's planning row carries the growth temperature band PlantProperties states and the resting day fraction.
public sealed class CropRowTests
{
    [Fact]
    public void ThePlanningRowDecodesThePlantsGrowthTemperatureBand()
    {
        var plant = new ThingDef
        {
            defName = "Plant_Devilstrand",
            plant = new PlantProperties
            {
                growDays = 22.5f,
                sowTags = ["Ground"],
                minGrowthTemperature = 0f,
                minOptimalGrowthTemperature = 10f,
                maxOptimalGrowthTemperature = 42f,
                maxGrowthTemperature = 58f,
            },
        };
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(plant, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        var catalog = new DefinitionCatalog(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, plant],
            stats,
            new CatalogDerived("Silver", "Wort", 10f));

        var row = PlanningViewReads.PlanningView(catalog, "Plant_Devilstrand")!;

        Assert.Equal([0.0, 10, 42, 58], new[] { row.MinGrowthTemperature.Value, row.MinOptimalGrowthTemperature.Value, row.MaxOptimalGrowthTemperature.Value, row.MaxGrowthTemperature.Value });
        Assert.Equal(22.5, row.GrowDays.Value);
        Assert.Equal(PlantResting.RestingDayFraction, row.RestingDayFraction.Value);
    }
}
