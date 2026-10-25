using System.Text.Json.Nodes;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlanningDefinitionTotalTests
{
    private static PlanningDefinition Rice => new()
    {
        Name = "Plant_Rice",
        Available = true,
        Plant = new()
        {
            GrowDays = 3, HarvestMinGrowth = 1, HarvestWork = 50,
            MinGrowthTemperature = 0, MinOptimalGrowthTemperature = 10,
            MaxOptimalGrowthTemperature = 30, MaxGrowthTemperature = 42,
            FertilitySensitivity = 1, RestingDayFraction = 0.45,
        },
        Harvest = new()
        {
            HarvestedThingDef = "RawRice", HarvestYield = 6,
            HarvestNutrition = 0.3, Edible = true, HarvestDestroysPlant = true,
            Decay = new HarvestDecay(40),
        },
    };

    [Fact]
    public void AnEdibleCropRequiresItsColonyDiet()
    {
        Assert.Throws<RequiredFactException>(() => Rice.CropChoice());
        var crop = (Rice with { Diet = new() { DietAllowed = true, NutritionDemandPerDay = 2.5 } }).CropChoice();
        Assert.Equal(2.5, crop.Demand);
        Assert.Equal(40, crop.Decay!.Days);
        Assert.Equal(6, crop.UnitsPerCell);
        Assert.Equal(1, crop.HarvestMinGrowth);
    }

    [Fact]
    public void ADefinitionWithoutAPlantDoesNotProvideCropFacts()
    {
        var wall = new PlanningDefinition { Name = "Wall", Available = true };
        Assert.Null(wall.Plant);
        Assert.Throws<RequiredFactException>(() => wall.CropChoice());
        Assert.Equal(PlanningAvailability.NotInCatalog, new PlanningDefinition { Name = "Absent" }.Availability);
    }

    [Fact]
    public void AFreeConstructionRecipeIsDistinctFromAMissingRecipe()
    {
        var free = new PlanningDefinition { Name = "SleepingSpot", Available = true, FixedConstruction = new() { Costs = [], WorkToBuild = 0 } };
        Assert.Empty(free.CheapestCosts());
        Assert.Throws<RequiredFactException>(() => (free with { FixedConstruction = null }).CheapestCosts());
    }

    [Fact]
    public void EveryColonyFrameRequiresTheCropDietSection()
    {
        var json = JsonNode.Parse(WireJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        Assert.True(json.Remove(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(nameof(ColonyFactsSnapshot.Crops))));
        Assert.Throws<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(json.ToJsonString()));
    }

    [Fact]
    public void AnInvalidGrowthCycleRaisesInsteadOfProducingAnEmptyRate()
    {
        var crop = (Rice with { Diet = new() { DietAllowed = true, NutritionDemandPerDay = 2.5 } }).CropChoice();
        Assert.True(CropGrowthModel.DailyGrowth(crop, 1, 20) > 0);
        Assert.Throws<RequiredFactException>(() => CropGrowthModel.DailyGrowth(crop with { GrowDays = double.NaN }, 1, 20));
    }

    [Fact]
    public void NonfoodGrowingHarvestsStillSupplyMaterialArrivals()
    {
        var cotton = Rice with
        {
            Name = "Plant_Cotton",
            Harvest = new() { HarvestedThingDef = "Cloth", HarvestYield = 6, HarvestDestroysPlant = true },
        };
        var farm = ZoneFixtures.Growing("cotton", cotton.Name, 8) with
        {
            PlantedCells = 8, FertilePlantedCells = 8, GrowthTotal = 4, FertilityFactorTotal = 8,
        };
        var calendar = new Calendar
        {
            Season = "Summer", DayOfYear = 1, GrowingDays = 60, GrowingDaysRemaining = 59,
        };
        var field = Assert.Single(ColonyProductionReads.ColonyFoodFields([farm], [cotton], calendar));
        Assert.False(field.Plan.Crop.Edible);
        Assert.Equal(new Resource("Cloth"), field.Plan.Crop.Harvests);
        Assert.NotNull(ClothingDemandRules.GrowingFieldCandidate(field.Plan.Crop.Harvests,
            field.Id, field.Plan.Sites.Cells * field.Plan.Crop.UnitsPerCell, field.RemainingGrowDays));
        Assert.Equal(8, field.Exposure.ZoneCells);
        Assert.Equal(8, field.Exposure.UnroofedCells);
    }

    [Fact]
    public void AGrowingPlantWithoutAProductOffersNoHarvestCapacityOrArrival()
    {
        var flowers = Rice with { Name = "Plant_Rose", Harvest = null };
        var farm = ZoneFixtures.Growing("flowers", flowers.Name, 8);
        Assert.Empty(ColonyProductionReads.ColonyFoodFields([farm], [flowers], new Calendar()));
        Assert.Empty(ColonyProductionReads.ColonyFieldCrops([farm], [flowers]));
        Assert.Throws<RequiredFactException>(() => ColonyProductionReads.ColonyFieldCrops([farm], []));
    }
}
