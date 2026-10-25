using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A growing zone's stand (#3949): plants with their means, or fallow. A fallow zone has no harvest to wait for.</summary>
public sealed class CropStandTests
{
    private static readonly RimGovernor.Brain.GameFacts.Observations.FoodClimate Summer = new()
    {
        GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day < 30 || day >= 50)),
        SowingNow = true,
        Season = "Summer",
        DayOfYear = 10,
        GrowingDays = 40,
        GrowingDaysRemaining = 30,
        GrowingDaysUntil = 0,
        NonGrowingDays = 20,
    };

    private static CropGrowth Growth(RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing observed) => new()
    {
        Observed = observed,
        MinGrowth = 6.0,
        MinOptimal = 10.0,
        MaxOptimal = 30.0,
        MaxGrowth = 42.0,
    };

    private static RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing Farm(uint planted, double temperature = 20) => new()
    {
        Id = "zone",
        Label = "Rice",
        Bounds = new RimGovernor.Brain.GameFacts.Observations.CellExtents { Minimum = new Cell(0, 0), Maximum = new Cell(2, 2) },
        Crop = "Plant_Rice",
        ZoneCells = 10, UnroofedCells = 10,
        UsableCells = 10,
        PlantedCells = planted,
        FertilePlantedCells = planted / 2,
        BlightedPlants = 0,
        EdibleCrop = true,
        GrowthTotal = planted * 0.5,
        FertilityFactorTotal = planted * 1.0,
        Temperature = temperature,
    };

    private static readonly PlanningDefinition[] Definitions =
    [
        new() { Name = "Plant_Rice", Plant = new PlanningPlant
        {
            MinGrowthTemperature = 6, MinOptimalGrowthTemperature = 10,
            MaxOptimalGrowthTemperature = 30, MaxGrowthTemperature = 42,
        } },
    ];

    [Fact]
    public void AFallowZoneHasNoGrowingCellsAndNoHarvestLead()
    {
        var growth = Growth(Farm(0));

        Assert.Equal(0L, growth.GrowingCells());
        Assert.IsType<HarvestTiming.Fallow>(CalendarRules.HarvestLeadDays(growth, 6.0, Summer));
    }

    [Fact]
    public void ASownZoneGrowsItsFertileCellsAndLeadsByTheRemainingGrowth()
    {
        var native = Farm(8) with { FertilePlantedCells = 8 };
        var growth = Growth(native);

        Assert.Same(native, growth.Observed);
        Assert.Equal(8L, growth.GrowingCells());
        Assert.Equal(3.0, Assert.IsType<HarvestTiming.Expected>(CalendarRules.HarvestLeadDays(growth, 6.0, Summer)).Days, 9);
    }

    [Fact]
    public void AFarmReadsAsFallowOrAsItsPlantMeans()
    {
        Assert.IsType<CropStand.Fallow>(ColonyProductionReads.FarmGrowth(Farm(0), Definitions).Stand);

        var native = Farm(10);
        var growth = ColonyProductionReads.FarmGrowth(native, Definitions);
        Assert.Same(native, growth.Observed);
        var sown = Assert.IsType<CropStand.Sown>(growth.Stand);
        Assert.Equal((5L, 0.5, 1.0), (sown.FertilePlanted, sown.GrowthMean, sown.FertilityMean));
    }

    [Fact]
    public void AnUnreadTemperatureRaisesInsteadOfReadingAsAnyValue()
    {
        Assert.Throws<RequiredFactException>(() => ColonyProductionReads.FarmGrowth(Farm(10, double.NaN), Definitions));
    }
}
