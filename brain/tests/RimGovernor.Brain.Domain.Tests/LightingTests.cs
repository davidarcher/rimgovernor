using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using LightingFacts = RimGovernor.Contracts.Wire.Observations.LightingFacts;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The lighting census is one total facts record (#3831): every work cell carries its measured glow, roof and room
/// plants, and every lamp its lit flag. On the wire it round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class LightingTests
{
    /// <summary>A census with no work cells and no lamps: the base every test lighting census starts from.</summary>
    internal static readonly LightingFacts NoLighting = new() { WorkCells = [], Lamps = [] };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = NoLighting with
        {
            WorkCells = [new WorkLightCell("Thing_ElectricStove3", new Cell(3, 4), 0.25, true, ["Plant_Agarilux"])],
            Lamps = [new LampState("Thing_TorchLamp5", false)],
        };

        var json = WireJson.Serialize(census);

        Assert.Equal(
            """{"workCells":[{"bench":"Thing_ElectricStove3","cell":{"x":3,"z":4},"glow":0.25,"roofed":true,"plantDefs":["Plant_Agarilux"]}],"lamps":[{"building":"Thing_TorchLamp5","lit":false}]}""",
            json);
        Assert.Equal(census, WireJson.Deserialize<LightingFacts>(json));
    }

    [Fact]
    public void ARoofedDarkCellIsDarkUnlessItsRoomGrowsALightDyingPlant()
    {
        var census = NoLighting with
        {
            WorkCells =
            [
                new WorkLightCell("bench-a", new Cell(1, 1), 0.1, true, []),
                new WorkLightCell("bench-b", new Cell(5, 5), 0.1, true, ["Plant_Agarilux", "Plant_Potato"]),
                new WorkLightCell("bench-c", new Cell(9, 9), 0.1, false, []),
            ],
        };
        var observation = new LightingObservation { Census = census, DiesToLight = ["Plant_Agarilux"], Lamps = [] };

        var review = LightingRules.ReviewLighting(observation, LightingPolicy.Default(), false);

        Assert.Equal(["bench-a"], review.Dark);
    }
}
