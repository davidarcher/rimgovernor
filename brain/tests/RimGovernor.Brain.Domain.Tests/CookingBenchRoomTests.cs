using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CookingBenchRoomTests
{
    [Fact]
    public void AnOutdoorButcherIsNotColocatedWithTheKitchen()
    {
        var shared = RoomFixtures.State("kitchen", contents: [new() { DefName = "ElectricStove", Units = 1 }, new() { DefName = "TableButcher", Units = 1 }]);
        var rooms = RoomFixtures.Census((shared, []));
        var inside = new CookingBench { Id = "butcher", Definition = "TableButcher", Usable = true, Room = "kitchen" };
        Assert.True(RoundsBuildingPlanner.ButchersAllColocated([inside], rooms));
        Assert.False(RoundsBuildingPlanner.ButchersAllColocated([inside with { Room = null }], rooms));
        Assert.False(RoundsBuildingPlanner.ButchersAllColocated([inside with { Room = "butchery" }], rooms));
    }
}
