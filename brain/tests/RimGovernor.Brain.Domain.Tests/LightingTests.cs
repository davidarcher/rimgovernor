using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using LightingFacts = RimGovernor.Brain.GameFacts.Observations.LightingFacts;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The lighting census is one total facts record (#3831): every work cell carries its measured glow, roof and room
/// plants, and every lamp its lit flag. On the wire it round-trips through <c>OrdersJsonContext</c> exactly.
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

        var json = OrdersJson.Serialize(census);

        Assert.Equal(
            """{"workCells":[{"bench":"Thing_ElectricStove3","cell":{"x":3,"z":4},"glow":0.25,"roofed":true,"plantDefs":["Plant_Agarilux"]}],"lamps":[{"building":"Thing_TorchLamp5","lit":false}]}""",
            json);
        Assert.Equal(census, OrdersJson.Deserialize<LightingFacts>(json));
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

    [Fact]
    public void LampRetainsNativeEvidenceAndDefersToItsObservedFuelState()
    {
        var native = new LampState("torch", false);
        var fuel = new BuildingFuel.Refuelable
        {
            Fuel = 0, TargetFuel = 30, OutOfFuel = true, Burn = BuildingServiceWire.IdleBurn,
        };
        var building = PowerBuildingFixtures.Trader("torch", "TorchLamp", new(2, 3), 0, 0, false) with
        {
            Service = BuildingServiceWire.NoService with { SwitchedOn = true, Fuel = fuel },
        };
        var lamp = new Lamp { Observed = native, Building = building, Radius = 5 };
        var census = NoLighting with
        {
            Lamps = [native], WorkCells = [new WorkLightCell("bench", new Cell(3, 3), .1, true, [])],
        };
        var observation = new LightingObservation { Census = census, DiesToLight = [], Lamps = [lamp] };
        var policy = LightingPolicy.Default();
        var review = LightingRules.ReviewLighting(observation, policy, false);

        Assert.Same(native, lamp.Observed);
        Assert.Same(building, lamp.Building);
        Assert.Same(fuel, lamp.Building.Service.Fuel);
        Assert.Equal(("torch", "TorchLamp", new Cell(2, 3)), (lamp.Id, lamp.Definition, lamp.Cell));
        Assert.Equal(LightingMethod.FuelNeeded, LightingRules.SelectLightingMethod(review, observation,
            new RimGovernor.Brain.Policy.LightingFacts { Rooms = RoomTestFacts.NoRooms }, policy).Method);

        var repaired = lamp with { Observed = native with { Lit = true }, Building = building with
        {
            Service = building.Service with { Fuel = fuel with { Fuel = 30, OutOfFuel = false } },
        } };
        Assert.Equal(LampFuel.Fueled, repaired.Fuel);
        Assert.True(repaired.Lit);
        Assert.Equal(LampFuel.Empty, lamp.Fuel);
        Assert.False(lamp.Lit);
        Assert.Equal(LightingMethod.Blocked, LightingRules.SelectLightingMethod(review,
            observation with { Lamps = [repaired] }, new RimGovernor.Brain.Policy.LightingFacts { Rooms = RoomTestFacts.NoRooms }, policy).Method);
    }
}
