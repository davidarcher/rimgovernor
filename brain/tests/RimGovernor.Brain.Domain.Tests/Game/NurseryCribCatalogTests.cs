using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class NurseryCribCatalogTests
{
    [Fact]
    public void TheSleepingCatalogIncludesCribsEvenThoughAdultFurnitureExcludesThem()
    {
        var rest = new StatDef { defName = nameof(StatDefOf.BedRestEffectiveness), valueIfMissing = .8f };
        var crib = new ThingDef
        {
            defName = "Crib", thingClass = typeof(Building_Bed), category = ThingCategory.Building,
            designationCategory = new DesignationCategoryDef { defName = "Furniture" },
            building = new BuildingProperties { bed_humanlike = true, bed_crib = true, bed_maxBodySize = .6f },
            statBases = [new() { stat = rest, value = .9f }],
        };
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(crib, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(crib, null, CatalogStats.Comfort)] = new(.75f, true, null),
                [(crib, null, CatalogStats.BedRestEffectiveness)] = new(.9f, true, null),
            },
            new Dictionary<BuildableDef, IReadOnlyList<string>> { [crib] = [] }, new HashSet<CatalogStat>(CatalogStats.All));
        var catalog = new DefinitionCatalog([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, rest, crib],
            stats, new CatalogDerived("Silver", "Wort", 10f));
        var row = Assert.Single(catalog.SleepingBeds());
        Assert.Equal("Crib", row.Def);
        Assert.Equal(.9, row.RestEffectiveness, 6);
        Assert.Equal(.75, row.Comfort, 6);
    }
}
