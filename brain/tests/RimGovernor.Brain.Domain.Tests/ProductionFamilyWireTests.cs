using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3776: the production, bill, recipe and stock-flow reads are plain required records; each round-trips exactly
// through the wire JSON.
public sealed class ProductionFamilyWireTests
{
    private static readonly O.ConsumptionSnapshot Consumption = new()
    {
        CurrentHour = 40,
        FirstHour = 2,
        Hours =
        [
            new O.ConsumptionHour { Hour = 38, Rows = [new O.ConsumptionRow { Definition = "WoodLog", Reason = "fuel_loaded", Count = 12 }] },
            new O.ConsumptionHour { Hour = 39, Rows = [new O.ConsumptionRow { Definition = "Shell_HighExplosive", Reason = "shell_loaded", Count = -1 }] },
        ],
    };

    private static readonly O.StorageOccupancySnapshot Occupancy = new()
    {
        CurrentHour = 40,
        Groups = [new O.StorageOccupancyGroup { Group = "Zone_3", Samples = [new O.StorageOccupancySample { Hour = 38, Stacks = 4 }, new O.StorageOccupancySample { Hour = 39, Stacks = 6 }] }],
    };

    private static readonly O.ResourceSourcesSnapshot Sources = new()
    {
        Sources =
        [
            new O.ResourceSource { Id = "Thing_MineableSteel1", DefName = "MineableSteel", Cell = new Cell(12, 30), Yield = 40, Buried = false, Designated = true, Safety = "open_surface", Distance = 7.5, Taken = false },
            new O.ResourceSource { Id = "Thing_MineableSteel2", DefName = "MineableSteel", Cell = new Cell(40, 2), Yield = 40, Buried = true, Designated = false, Safety = "supported_roof", Distance = 31, Taken = true },
        ],
        Storage = new O.StorageCapacity { Capacity = 750, Stored = 120, Haulers = [new PawnId("Thing_Human7")], Candidates = [new Cell(5, 6), new Cell(5, 7)] },
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        var back = WireJson.Deserialize<T>(json);
        Assert.NotNull(back);
        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void TheConsumptionRingRoundTripsExactly() => RoundTrips(Consumption);

    [Fact]
    public void TheOccupancyRingRoundTripsExactly() => RoundTrips(Occupancy);

    [Fact]
    public void ResourceSourcesRoundTripExactly() => RoundTrips(Sources);
}
