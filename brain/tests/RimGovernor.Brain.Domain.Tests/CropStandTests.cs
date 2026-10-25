using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A growing zone's stand (#3949): plants with their means, or fallow. A fallow zone has no harvest to wait for.</summary>
public sealed class CropStandTests
{
    private static readonly Calendar Summer = new()
    {
        Season = "Summer",
        DayOfYear = 10,
        GrowingDays = 40,
        GrowingDaysRemaining = 30,
        GrowingDaysUntil = 0,
        NonGrowingDays = 20,
    };

    private static CropGrowth Growth(CropStand stand, double temperature = 20) => new()
    {
        Stand = stand,
        Temperature = temperature,
        MinGrowth = Known(6.0),
        MinOptimal = Known(10.0),
        MaxOptimal = Known(30.0),
        MaxGrowth = Known(42.0),
    };

    private static O.ZoneState.Growing Farm(uint planted, double temperature = 20) => new()
    {
        Id = "zone",
        Label = "Rice",
        Bounds = new O.CellExtents { Minimum = new Cell(0, 0), Maximum = new Cell(2, 2) },
        Crop = "Plant_Rice",
        UsableCells = 10,
        PlantedCells = planted,
        FertilePlantedCells = planted / 2,
        BlightedPlants = 0,
        EdibleCrop = true,
        GrowthTotal = planted * 0.5,
        FertilityFactorTotal = planted * 1.0,
        Temperature = temperature,
    };

    [Fact]
    public void AFallowZoneHasNoGrowingCellsAndNoHarvestLead()
    {
        var growth = Growth(new CropStand.Fallow());

        Assert.Equal(0L, growth.GrowingCells().Value);
        Assert.False(CalendarRules.HarvestLeadDays(growth, Known(6.0), Known(Summer)).TryGetValue(out _));
    }

    [Fact]
    public void ASownZoneGrowsItsFertileCellsAndLeadsByTheRemainingGrowth()
    {
        var growth = Growth(new CropStand.Sown(10, 8, 0.5, 1.0));

        Assert.Equal(8L, growth.GrowingCells().Value);
        Assert.Equal(3.0, CalendarRules.HarvestLeadDays(growth, Known(6.0), Known(Summer)).Value, 9);
    }

    [Fact]
    public void AFarmReadsAsFallowOrAsItsPlantMeans()
    {
        Assert.IsType<CropStand.Fallow>(ColonyProductionReads.FarmGrowth(Farm(0), []).Stand);

        var sown = Assert.IsType<CropStand.Sown>(ColonyProductionReads.FarmGrowth(Farm(10), []).Stand);
        Assert.Equal((10L, 5L, 0.5, 1.0), (sown.Planted, sown.FertilePlanted, sown.GrowthMean, sown.FertilityMean));
    }

    [Fact]
    public void AnUnreadTemperatureRaisesInsteadOfReadingAsAnyValue()
    {
        Assert.Throws<RequiredFactException>(() => ColonyProductionReads.FarmGrowth(Farm(10, double.NaN), []));
    }
}
