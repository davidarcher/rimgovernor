using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The shared native zone census rows the tests stand zones with: each case with every required member.</summary>
internal static class ZoneFixtures
{
    /// <summary>The bounds every fixture zone reports: the one cell at the origin.</summary>
    public static readonly Observations.CellExtents Origin = new() { Minimum = new Cell(0, 0), Maximum = new Cell(0, 0) };

    /// <summary>A growing zone sowing <paramref name="crop"/> with nothing planted yet.</summary>
    public static Observations.ZoneState.Growing Growing(string id, string crop, uint usableCells = 0, bool edible = false, double temperature = 21) => new()
    {
        Id = id,
        Label = id,
        Bounds = Origin,
        Crop = crop,
        UsableCells = usableCells,
        PlantedCells = 0,
        FertilePlantedCells = 0,
        BlightedPlants = 0,
        EdibleCrop = edible,
        GrowthTotal = 0,
        FertilityFactorTotal = 0,
        Temperature = temperature,
    };

    /// <summary>A stockpile zone with no filter read.</summary>
    public static Observations.ZoneState.Stockpile Stockpile(string id, string priority = "Normal", bool foodStorage = false) => new()
    {
        Id = id,
        Label = id,
        Bounds = Origin,
        Priority = priority,
        FoodStorage = foodStorage,
    };
}
