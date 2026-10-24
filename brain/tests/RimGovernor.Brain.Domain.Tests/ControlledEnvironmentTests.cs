using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The controlled-growing census reads whole (#3921): the wire rows are required, a light's room and network and a
/// grower's crop and sow tag are real none relations, a grower's fertility is a named state, and the network headroom
/// is plain arithmetic.
/// </summary>
public sealed class ControlledEnvironmentTests
{
    /// <summary>A planning region with no lamps, growers, rooms or networks: the census every test without one carries.</summary>
    internal static readonly O.ControlledEnvironment NoWireEnvironment = new() { Lights = [], Growers = [], Rooms = [], Networks = [] };

    /// <summary>The decoded census of <see cref="NoWireEnvironment"/> at a mild outdoor temperature.</summary>
    internal static readonly ControlledEnvironment NoEnvironment = new() { Lights = [], Growers = [], Rooms = [], Networks = [], OutdoorTemperatureC = 15 };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = new O.ControlledEnvironment
        {
            Lights =
            [
                new O.GrowLight { Building = new C.Ref { Id = "Thing_SunLamp4" }, Room = new C.Ref { Id = "12" }, GrowthCells = [new Cell(3, 4)], LitNow = true, PowerNetId = "77" },
                new O.GrowLight { Building = new C.Ref { Id = "Thing_SunLamp5" }, Room = null, GrowthCells = [], LitNow = false, PowerNetId = null },
            ],
            Growers =
            [
                new O.PlantGrower { Building = new C.Ref { Id = "Thing_HydroponicsBasin8" }, CropDefName = "Plant_Rice", CanSow = true },
                new O.PlantGrower { Building = new C.Ref { Id = "Thing_HydroponicsBasin9" }, CropDefName = null, CanSow = false },
            ],
            Rooms = [new O.GrowRoom { Room = new C.Ref { Id = "12" }, GridRoom = 4, TemperatureC = -2.5 }],
            Networks = [new O.PowerHeadroom { Id = "77", GenerationW = 1000, SolarW = 400, WindW = 300, ConsumptionW = 200, CapacityWattDays = 0, HasActiveSource = true }],
        };

        var json = WireJson.Serialize(census);

        Assert.Equal(
            """{"lights":[{"building":{"id":"Thing_SunLamp4"},"room":{"id":"12"},"growthCells":[{"x":3,"z":4}],"litNow":true,"powerNetId":"77"},{"building":{"id":"Thing_SunLamp5"},"room":null,"growthCells":[],"litNow":false,"powerNetId":null}],"growers":[{"building":{"id":"Thing_HydroponicsBasin8"},"cropDefName":"Plant_Rice","canSow":true},{"building":{"id":"Thing_HydroponicsBasin9"},"cropDefName":null,"canSow":false}],"rooms":[{"room":{"id":"12"},"gridRoom":4,"temperatureC":-2.5}],"networks":[{"id":"77","generationW":1000,"solarW":400,"windW":300,"consumptionW":200,"capacityWattDays":0,"hasActiveSource":true}]}""",
            json);
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
        Available = Known(true),
        Edible = Known(true),
        GrowDays = Known(6.0),
        FertilityMin = Known(0.5),
        FertilitySensitivity = Known(1.0),
        HarvestNutrition = Known(0.05),
        Demand = Known(1.6),
        SowTags = Known(EquatableArray.From(["Ground", "Hydroponic"])),
        MinGlow = Known(0.3),
        DietAllowed = Known(true),
    };
}
