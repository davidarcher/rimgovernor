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
        // The crib row a newborn's crib is prepared from (RG-HABITAT-007); a catalog without one names none.
        Assert.Equal("Crib", catalog.CribDefinition());
        Assert.Equal("", new DefinitionCatalog([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, rest], stats, new CatalogDerived("Silver", "Wort", 10f)).CribDefinition());
    }

    [Fact]
    public void TheNewbornsRestRatesAreReadFromTheDefsWithNoBabyPresent()
    {
        // 1.7 Core: RestFallRateFactor and RestRateMultiplier default to 1, the ground rests at BedRestEffectiveness's 0.8
        // valueIfMissing, and neither Human nor HumanlikeBaby lists a rest stat; a stage factor here checks the order.
        var fall = new StatDef { defName = nameof(StatDefOf.RestFallRateFactor), defaultBaseValue = 1, minValue = .0001f };
        var rate = new StatDef { defName = nameof(StatDefOf.RestRateMultiplier), defaultBaseValue = 1, minValue = .05f };
        var bed = new StatDef { defName = nameof(StatDefOf.BedRestEffectiveness), valueIfMissing = .8f };
        var human = new ThingDef { defName = nameof(ThingDefOf.Human), statBases = [new() { stat = rate, value = 1.5f }] };
        var baby = new LifeStageDef { defName = nameof(LifeStageDefOf.HumanlikeBaby), statFactors = [new() { stat = fall, value = 2f }] };
        var stats = new DefStatTable(new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>(), new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
            new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All));
        DefinitionCatalog Catalog(params Def[] defs) => new([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, .. defs], stats, new CatalogDerived("Silver", "Wort", 10f));

        var rest = Catalog(fall, rate, bed, human, baby).NewbornRest();
        Assert.NotNull(rest);
        Assert.Equal(1.5833333E-05 * 2, rest.FallPerTick, 9);
        Assert.Equal(Need_Rest.BaseRestGainPerTick * .8 * 1.5, rest.UnservedGainPerTick, 9);
        Assert.Equal(Need_Rest.BaseRestGainPerTick * 1.5, rest.GainPerTickPerEffectiveness, 9);
        // Without the baby life stage (or the race, or a rest stat) the rates are unread, never guessed.
        Assert.Null(Catalog(fall, rate, bed, human).NewbornRest());
    }
}
