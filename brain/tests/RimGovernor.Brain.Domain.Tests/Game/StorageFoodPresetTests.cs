using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Store.Codec;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class StorageFoodPresetTests
{
    [Fact]
    public void MealShelfRecognizesInstalledMealTypesWithoutAnIdentityAllowlist()
    {
        var meal = new ThingDef { defName = "InstalledMeal", ingestible = new IngestibleProperties { foodType = FoodTypeFlags.Meal } };
        Assert.True(DefinitionCatalog.MealStorageFood(meal));
        Assert.False(DefinitionCatalog.MealStorageFood(new ThingDef { ingestible = new IngestibleProperties { foodType = FoodTypeFlags.VegetableOrFruit } }));
        var preset = StockpileFilter.MealShelfFilter();
        Assert.Equal(FilterBase.MealShelf, StoreJson.Deserialize<StockpileFilter>(StoreJson.Serialize(preset)).Base);
        Assert.Empty(preset.Allow);
    }

    [Fact]
    public void ColdStorageUsesRotAndCategoryFactsIncludingNestedInsectCorpses()
    {
        var foods = new ThingCategoryDef { defName = nameof(ThingCategoryDefOf.Foods) };
        var animals = new ThingCategoryDef { defName = nameof(ThingCategoryDefOf.CorpsesAnimal) };
        var insects = new ThingCategoryDef { defName = "InstalledInsectCorpses", parent = animals };
        ThingDef Item(ThingCategoryDef category, bool rots) => new()
        {
            defName = "InstalledFood", thingCategories = [category],
            comps = rots ? [new CompProperties_Rottable()] : [],
        };
        Assert.True(DefinitionCatalog.ColdStorageFood(Item(foods, true)));
        Assert.True(DefinitionCatalog.ColdStorageFood(Item(insects, true)));
        Assert.False(DefinitionCatalog.ColdStorageFood(Item(foods, false)));
        Assert.False(DefinitionCatalog.ColdStorageFood(Item(new ThingCategoryDef { defName = nameof(ThingCategoryDefOf.CorpsesHumanlike) }, true)));
        var preset = StockpileFilter.PerishablesFilter();
        var roundTrip = StoreJson.Deserialize<StockpileFilter>(StoreJson.Serialize(preset));
        Assert.Equal(FilterBase.ColdFood, roundTrip.Base);
        Assert.Empty(roundTrip.Allow);
        Assert.Equal([FilterSelector.SpecialFilter("AllowRotten")], roundTrip.Disallow);
    }
}
