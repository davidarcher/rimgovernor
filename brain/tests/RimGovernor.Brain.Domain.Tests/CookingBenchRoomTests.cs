using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CookingBenchRoomTests
{
    [Fact]
    public void AnOutdoorButcherIsNotColocatedWithTheKitchen()
    {
        var shared = RoomFixtures.State("kitchen", contents: [new() { DefName = "ElectricStove", Units = 1 }, new() { DefName = "TableButcher", Units = 1 }]);
        var rooms = RoomFixtures.Census((shared, []));
        var inside = ProductionBenchFixtures.Butchering("butcher", "TableButcher", "butcher-token", true, "kitchen");
        Assert.True(RoundsBuildingPlanner.ButchersAllColocated([inside], rooms));
        Assert.False(RoundsBuildingPlanner.ButchersAllColocated([ProductionBenchFixtures.InRoom(inside, null)], rooms));
        Assert.False(RoundsBuildingPlanner.ButchersAllColocated([ProductionBenchFixtures.InRoom(inside, "butchery")], rooms));
    }
}
