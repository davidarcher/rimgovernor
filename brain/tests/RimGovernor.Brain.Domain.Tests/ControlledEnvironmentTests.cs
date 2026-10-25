using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The controlled-growing census reads whole (#3921): the wire rows are required, a light's room and network and a
/// grower's crop and sow tag are real none relations, a grower's fertility is a named state, and the network headroom
/// is plain arithmetic.
/// </summary>
public sealed class ControlledEnvironmentTests
{
    /// <summary>A complete mild census with no growing infrastructure.</summary>
    internal static readonly ControlledEnvironment NoEnvironment = new() { Lights = [], Growers = [], Rooms = [], Networks = [], OutdoorTemperatureC = 15 };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = new O.ControlledEnvironment
        {
            OutdoorTemperatureC = -3,
            Lights =
            [
                new O.GrowLight { Id = "Thing_SunLamp4", Room = "12", GrowthCells = [new Cell(3, 4)], LitNow = true, Network = "77" },
                new O.GrowLight { Id = "Thing_SunLamp5", Room = null, GrowthCells = [], LitNow = false, Network = null },
            ],
            Growers =
            [
                new O.PlantGrower { Id = "Thing_HydroponicsBasin8", Fertility = new GrowerFertility.Fixed(2.8), SowTag = "Hydroponic", Crop = "Plant_Rice", CanSow = true },
                new O.PlantGrower { Id = "Thing_HydroponicsBasin9", Fertility = new GrowerFertility.Ground(), SowTag = null, Crop = null, CanSow = false },
            ],
            Rooms = [new O.GrowRoom { Id = "12", GridRoom = 4, TemperatureC = -2.5 }],
            Networks = [new O.PowerHeadroom { Id = "77", GenerationW = 1000, SolarW = 400, WindW = 300, ConsumptionW = 200, CapacityWattDays = 0, ActiveSource = true }],
        };

        var json = WireJson.Serialize(census);

        Assert.Contains(""""kind":"fixed"""", json);
        Assert.Contains(""""kind":"ground"""", json);
        Assert.Equal(census, WireJson.Deserialize<O.ControlledEnvironment>(json));
    }

    [Fact]
    public void HeadroomIsDayNightAndCalmNight()
    {
        var network = new PowerHeadroom { Id = "77", GenerationW = 1000, SolarW = 400, WindW = 300, ConsumptionW = 200, CapacityWattDays = 0, ActiveSource = true };

        Assert.Equal(800, network.DayHeadroomW());
        Assert.Equal(400, network.NightHeadroomW());
        Assert.Equal(100, network.CalmNightHeadroomW());
    }

    [Fact]
    public void SitePowerChargesAStillNightWithoutBatteriesAndSkipsADeadNetwork()
    {
        var env = NoEnvironment with
        {
            Networks =
            [
                new PowerHeadroom { Id = "a", GenerationW = 1000, SolarW = 400, WindW = 300, ConsumptionW = 200, CapacityWattDays = 0, ActiveSource = true },
                new PowerHeadroom { Id = "b", GenerationW = 5000, SolarW = 0, WindW = 0, ConsumptionW = 0, CapacityWattDays = 0, ActiveSource = false },
            ],
        };

        var (network, room, fits) = SiteRules.SitePower(env, 150, true);

        Assert.Equal("a", network?.Id);
        Assert.Equal(100, room);
        Assert.False(fits);
        Assert.True(SiteRules.SitePower(env, 150, false).Fits);
        Assert.Null(SiteRules.SitePower(NoEnvironment, 0, false).Network);
    }

    [Fact]
    public void OnlyALitLampGrowsCells()
    {
        var env = NoEnvironment with
        {
            Lights =
            [
                new GrowLight { Id = "lamp-b", Room = null, Network = null, LitNow = true, GrowthCells = [new Cell(1, 1)] },
                new GrowLight { Id = "lamp-a", Room = "12", Network = "77", LitNow = true, GrowthCells = [new Cell(1, 1)] },
                new GrowLight { Id = "lamp-c", Room = "12", Network = "77", LitNow = false, GrowthCells = [new Cell(2, 2)] },
            ],
        };

        var lit = env.LitCells();

        Assert.Equal("lamp-a", Assert.Single(lit).Value);
    }

    [Fact]
    public void AGrowerWithFixedFertilityIsReCroppedAndOneOnTheGroundIsLeftAlone()
    {
        var fixedGrower = Grower("basin-1") with { Fertility = new GrowerFertility.Fixed(2.8) };
        var request = new GrowerCropRequest
        {
            Choices = [Rice],
            Growers =
            [
                fixedGrower,
                Grower("basin-2") with { Fertility = new GrowerFertility.Ground() },
                Grower("basin-3") with { Crop = null },
                Grower("basin-4") with { SowTag = null },
                Grower("basin-5") with { CanSow = false },
            ],
        };

        var choice = Assert.Single(GrowerCropRules.PlanGrowerCrops(request));

        Assert.Equal("basin-1", choice.Grower);
        Assert.Equal("Plant_Strawberry", choice.Current);
        Assert.Equal("Rice", choice.Crop.Name);
    }

    private static PlantGrower Grower(string id) => new()
    {
        Id = id,
        Fertility = new GrowerFertility.Fixed(2.8),
        SowTag = "Hydroponic",
        Crop = "Plant_Strawberry",
        CanSow = true,
    };

    private static readonly CropChoice Rice = new()
    {
        Name = "Rice",
        Available = true,
        Edible = true,
        GrowDays = 6.0,
        FertilityMin = 0.5,
        FertilitySensitivity = 1.0,
        HarvestNutrition = 0.05,
        Demand = 1.6,
        SowTags = EquatableArray.From(["Ground", "Hydroponic"]),
        MinGlow = 0.3,
        DietAllowed = true,
    };
}
