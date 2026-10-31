using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Task 5.4 of <c>native-led-labor-execution</c>: a construction stall is observed from two or more native progress
/// readings a whole window apart, never fabricated from ticks: changed progress or a rewind reopens the window, native work
/// targeting the site restarts its quiet span, and a missing read observes nothing.</summary>
public sealed class ConstructionStallsTests
{
    private static ConstructionActivityRead Read(long tick, params ConstructionSiteProgress[] progress) =>
        ConstructionFixtures.NoConstruction(tick) with { Progress = EquatableArray.From(progress) };

    private static ConstructionSiteProgress Site(int delivered = 0, double work = 0, bool worked = false, string site = "blueprint") =>
        new(site, delivered, 20, work, worked);

    private static ConstructionCalibration.Observed Step(ref EquatableArray<ConstructionProgressWindow> windows, ConstructionActivityRead read)
    {
        var observed = Assert.IsType<ConstructionCalibration.Observed>(ConstructionStalls.Observe(windows, read));
        windows = observed.Windows;
        return observed;
    }

    [Fact]
    public void AnUnworkedSiteWhoseProgressHoldsAWholeWindowIsAStallOfItsCause()
    {
        EquatableArray<ConstructionProgressWindow> windows = [];
        Assert.Empty(Step(ref windows, Read(100, Site())).Stalls);
        Assert.Empty(Step(ref windows, Read(100 + ConstructionStalls.WindowTicks - 1, Site())).Stalls);
        var stall = Assert.Single(Step(ref windows, Read(100 + ConstructionStalls.WindowTicks, Site())).Stalls);
        Assert.Equal(new ConstructionStall("blueprint", ConstructionStallCause.SupplyUnstarted, 100 + ConstructionStalls.WindowTicks), stall);

        // A supplied frame whose work done holds is a finish stall.
        windows = [];
        Step(ref windows, Read(100, Site(delivered: 20, work: 5)));
        Assert.Equal(ConstructionStallCause.FinishUnstarted,
            Assert.Single(Step(ref windows, Read(100 + ConstructionStalls.WindowTicks, Site(delivered: 20, work: 5))).Stalls).Cause);
    }

    [Fact]
    public void ASingleReadingIsNeverAStall()
    {
        // A first reading opens its window at its own tick, however late in the game it is read.
        var observed = Assert.IsType<ConstructionCalibration.Observed>(ConstructionStalls.Observe([], Read(10 * ConstructionStalls.WindowTicks, Site())));
        Assert.Empty(observed.Stalls);
        Assert.Equal(10 * ConstructionStalls.WindowTicks, Assert.Single(observed.Windows).Opened);
    }

    [Fact]
    public void ChangedProgressOrARewindReopensTheWindow()
    {
        EquatableArray<ConstructionProgressWindow> windows = [];
        Step(ref windows, Read(100, Site()));
        Step(ref windows, Read(5000, Site(delivered: 10)));
        Assert.Empty(Step(ref windows, Read(100 + ConstructionStalls.WindowTicks, Site(delivered: 10))).Stalls);
        Assert.Equal(5000, Assert.Single(windows).Opened);
        Step(ref windows, Read(9000, Site(delivered: 10, work: 1)));
        Assert.Equal(9000, Assert.Single(windows).Opened);
        // An earlier tick (a reload) reopens the window at that tick rather than counting back across it.
        Assert.Empty(Step(ref windows, Read(50, Site(delivered: 10, work: 1))).Stalls);
        Assert.Equal((50L, 50L), (Assert.Single(windows).Opened, Assert.Single(windows).Quiet));
    }

    [Fact]
    public void NativeWorkTargetingTheSiteRestartsItsQuietSpanWithoutReopeningIt()
    {
        EquatableArray<ConstructionProgressWindow> windows = [];
        Step(ref windows, Read(100, Site()));
        Assert.Empty(Step(ref windows, Read(3000, Site(worked: true))).Stalls);
        Assert.Equal((100L, 3000L), (Assert.Single(windows).Opened, Assert.Single(windows).Quiet));
        // Worked at the window's end is no stall; unworked a whole span after the last work is.
        Assert.Empty(Step(ref windows, Read(100 + ConstructionStalls.WindowTicks, Site(worked: true))).Stalls);
        Assert.Empty(Step(ref windows, Read(100 + 2 * ConstructionStalls.WindowTicks - 1, Site())).Stalls);
        Assert.Single(Step(ref windows, Read(100 + 2 * ConstructionStalls.WindowTicks, Site())).Stalls);
    }

    [Fact]
    public void ASiteNoLongerReadDropsItsWindowAndAMissingReadObservesNothing()
    {
        EquatableArray<ConstructionProgressWindow> windows = [];
        Step(ref windows, Read(100, Site(), Site(site: "other")));
        Assert.Equal(["blueprint", "other"], windows.Select(w => w.Site));
        Step(ref windows, Read(200, Site(site: "other")));
        Assert.Equal("other", Assert.Single(windows).Site);
        Assert.Equal(new ConstructionCalibration.Unobserved(ConstructionCalibration.UnobservedReason), ConstructionStalls.Observe(windows, null));
    }
}
