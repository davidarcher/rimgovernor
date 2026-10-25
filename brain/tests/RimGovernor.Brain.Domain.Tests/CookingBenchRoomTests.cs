using RimGovernor.Brain.Runtime;
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
        Assert.True(BuildingPlanner.ButchersAllColocated([inside], rooms));
        Assert.False(BuildingPlanner.ButchersAllColocated([ProductionBenchFixtures.InRoom(inside, null)], rooms));
        Assert.False(BuildingPlanner.ButchersAllColocated([ProductionBenchFixtures.InRoom(inside, "butchery")], rooms));
    }
}
