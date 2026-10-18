using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Growth shortfall (#3022): a standing suite is replaced only when its group is short by at least one station's
/// cells, and a suite group is matched to its own standing room before rooms are matched by size.
/// </summary>
public sealed class GroupOutgrownTests
{
    /// <summary>A suite group of one two-cell bed: Kept is the floor of the suite standing for it, 0 for a new wish.</summary>
    private static RoomGroup Suite(int id, int area, int kept) => new()
    {
        Id = id,
        Role = RoomRole.Suite,
        Planned = PlannedRole.Suite,
        Stations = [new GroupStation("bed" + id, "Bed", StationPurpose.Sleep, 1)],
        Footprint = 2,
        Area = area,
        Kept = kept,
    };

    /// <summary>A standing 4x5 suite (20 cells).</summary>
    private static BuiltRegion Standing(int room, int x) => new()
    {
        Room = room,
        Role = RoomRole.Suite,
        Planned = PlannedRole.Suite,
        Region = RegionRules.RectRegion(new(x, 0, 4, 5)),
    };

    private static bool Outgrown(RoomGroup g)
    {
        var m = Assert.Single(LayoutStationPlanRules.MatchGroupRooms([g], [Standing(7, 0)]));
        return LayoutStationPlanRules.GroupOutgrown(g, m);
    }

    [Fact]
    public void AOneCellTargetRaiseDoesNotReplaceAStandingSuite() => Assert.False(Outgrown(Suite(0, 21, 20)));

    [Theory]
    [InlineData(22)]
    [InlineData(30)]
    public void AShortfallOfAStationOrMoreReplacesTheStandingSuite(int area) => Assert.True(Outgrown(Suite(0, area, 20)));

    [Fact]
    public void ANewLargerSuiteWishDoesNotTakeAnotherSuitesSmallerStandingRoom()
    {
        RoomGroup[] groups = [Suite(0, 30, 0), Suite(1, 20, 20)];
        var matched = LayoutStationPlanRules.MatchGroupRooms(groups, [Standing(7, 0)]);
        Assert.Empty(matched[0].Rooms);
        Assert.Equal([7], matched[1].Rooms);
        Assert.False(LayoutStationPlanRules.GroupOutgrown(groups[1], matched[1]));
    }
}
