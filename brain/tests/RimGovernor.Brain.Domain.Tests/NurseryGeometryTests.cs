using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NurseryGeometryTests
{
    private static NurseryGeometryRead Read(params NurseryRegionRead[] regions) =>
        new(5, 1, [0, 1, 2, 3, 4], [true, true, true, true, true], [.. regions], 9.9, 6, 2);

    [Fact]
    public void SoundCountsOrdinaryRegionsAndProcessesDoorwaysWithoutSpendingTheCap()
    {
        var geometry = new ForecastNurseryGeometry(Read(
            new(0, false, true, 10, [1]), new(1, true, true, 11, [0, 2]),
            new(2, false, true, 12, [1, 3]), new(3, true, true, 13, [2, 4]), new(4, false, true, 14, [3])));
        Assert.True(geometry.Hears(new(0, 0), new(2, 0), 1));
        Assert.False(geometry.Hears(new(0, 0), new(3, 0), 1));
    }

    [Fact]
    public void OpeningTheNurseryDoorChangesReachAndHearingCapacityStillLimitsDistance()
    {
        var geometry = new ForecastNurseryGeometry(Read(
            new(0, false, true, 10, [1]), new(1, true, false, 11, [0, 2]),
            new(2, false, true, 12, [1]), new(3, false, true, 13, []), new(4, false, true, 14, [])));
        Assert.False(geometry.Hears(new(0, 0), new(2, 0), 1));
        Assert.True(geometry.Hears(new(0, 0), new(2, 0), 1, 10));
        Assert.False(geometry.Hears(new(0, 0), new(2, 0), .1, 10));
        Assert.False(geometry.Hears(new(0, 0), new(0, 0), 0));
    }

    [Fact]
    public void SocialChecksInteriorVisibilityRatherThanEndpointWalkability()
    {
        var clear = Read() with { SeeOver = [false, true, true, true, false] };
        Assert.True(new ForecastNurseryGeometry(clear).Social(new(0, 0), new(4, 0)));
        Assert.False(new ForecastNurseryGeometry(clear with { SeeOver = [false, true, false, true, false] }).Social(new(0, 0), new(4, 0)));
    }
}
