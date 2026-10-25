using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class PlanningGrowerTests
{
    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(true, 2.8f)]
    public void PlanningUsesTheGrowerTypeAndPreservesNativeFertilityStates(bool grower, float fertility)
    {
        var building = new ThingDef
        {
            defName = "SowTaggedBuilding", thingClass = grower ? typeof(Building_PlantGrower) : typeof(Building_WorkTable),
            designationCategory = new DesignationCategoryDef(), fertility = fertility,
            building = new BuildingProperties { sowTag = "SupportPlantsOnly" },
        };
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(building, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(building, null, CatalogStats.WorkToBuild)] = new(0, true, null),
            }, new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All));
        var catalog = new DefinitionCatalog([building, new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }],
            stats, new CatalogDerived("Silver", "Wort", 10f));

        var view = Assert.IsType<RimGovernor.Brain.Policy.PlanningDefinition>(PlanningViewReads.PlanningView(catalog, building.defName));
        if (!grower) Assert.Null(view.Grower);
        else if (fertility < 0) Assert.IsType<GrowerFertility.Ground>(view.Grower);
        else Assert.Equal(fertility, Assert.IsType<GrowerFertility.Fixed>(view.Grower).Value);
        Assert.Equal(0, view.FixedConstruction!.WorkToBuild);
    }
}
