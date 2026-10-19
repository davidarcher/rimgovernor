using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Store;
using Xunit;
using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3198: a speed-matrix row reloads the stage under a kept journal, whose revision is no row marker: resuming writes a
/// disabled review carrying the previous row's world and tick under a new revision, and a relaunched game restarts the
/// count below the previous run's. The row's tick-budget wait reads only an enabled review of the row's own load.
/// </summary>
public sealed class SpeedmatrixReviewedTickTests
{
    private const ulong Reload = 0;

    private static readonly Common.Identity Row = new() { ColonyId = "colony", LoadToken = "row-load", MapId = 0 };

    private static GenerationSnapshot World(string load) =>
        new(new ColonyId("colony"), new MapId(0), new LoadId(load), new PlanId("root"), new PlanRevision(1), new NativeGeneration(1));

    [Fact]
    public void TheResumesDisabledReviewKeepsTheReloadTick()
    {
        // The observation-load row's journal right after resume: the uncapped row's world and last tick, disabled.
        var carried = new Rounds { Revision = 5, Snapshot = World("previous-load"), Tick = new Tick(60_000), Enabled = false };

        Assert.Equal(Reload, SpeedmatrixRun.ReviewedTick(carried, Row, Reload));
    }

    [Fact]
    public void ThePreviousRowsEnabledReviewKeepsTheReloadTick()
    {
        var previous = new Rounds { Revision = 4, Snapshot = World("previous-load"), Tick = new Tick(60_000), Enabled = true };

        Assert.Equal(Reload, SpeedmatrixRun.ReviewedTick(previous, Row, Reload));
    }

    [Fact]
    public void TheRowsEnabledReviewIsTheRowsTickWhateverItsRevision()
    {
        // A relaunched game's journal restarts at revision 1, below the kept journal's last revision.
        var reviewed = new Rounds { Revision = 1, Snapshot = World("row-load"), Tick = new Tick(9_600), Enabled = true };

        Assert.Equal(9_600UL, SpeedmatrixRun.ReviewedTick(reviewed, Row, Reload));
    }
}
