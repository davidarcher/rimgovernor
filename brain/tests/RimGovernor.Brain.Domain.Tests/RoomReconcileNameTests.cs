using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Every room reconcile is named "&lt;role&gt;-shell-room&lt;id&gt;" (RoomReconcileName, #3741), so the in-flight census
/// (ColonyReview.RoomMethodIds) finds the room's open work by its id: the morgue, tomb, jail, envelope, range and ring
/// were "&lt;role&gt;-room&lt;id&gt;" and never counted.
/// </summary>
public sealed class RoomReconcileNameTests
{
    private static readonly PlannedRoom Room = new() { Id = 7 };

    private static PlanState Open(string method) => new(false, default, new MethodId(method), new PlanSpec(new("plan"), new(1), []), default, default);

    [Theory]
    [InlineData("morgue")]
    [InlineData("tomb")]
    [InlineData("jail")]
    [InlineData("envelope")]
    [InlineData("range")]
    [InlineData("ring")]
    [InlineData("bedroom")]
    public void TheCensusFindsEveryRoomReconcileByItsRoomId(string role)
    {
        var name = SleepingUpkeepPlanner.RoomReconcileName(role, Room);

        Assert.Equal($"{role}-shell-room7", name);
        Assert.Equal([7], ColonyReview.RoomMethodIds([Open(name), Open(name + "-build-0a1b")]));
    }

    [Fact]
    public void TheCensusFindsEveryPlannedRolesShellIncludingDashedRoles()
    {
        foreach (var role in Enum.GetValues<PlannedRole>().Where(r => r != PlannedRole.None))
        {
            var method = BuildingPlanner.PlannedRoomMethod(Room with { Role = role }).Value;
            Assert.True(ColonyReview.RoomMethodIds([Open(method + "-build-0a1b")]).SetEquals([7]), method);
        }
    }

    [Fact]
    public void ARetiredPlanIsNotInFlight() =>
        Assert.Empty(ColonyReview.RoomMethodIds([Open(SleepingUpkeepPlanner.RoomReconcileName("morgue", Room)) with { Retired = true }]));
}
