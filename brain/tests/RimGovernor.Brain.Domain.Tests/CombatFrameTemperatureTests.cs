using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatFrameTemperatureTests
{
    private static FrameView View(ColonyFactsSnapshot? colony) =>
        new(1, new NativeFrame(new BundleSnapshot { Context = new ObservationContext { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, ColonyFacts = colony }, 0, 0));

    [Fact]
    public void TheCombatStateTakesTheFramesOutdoorTemperatureAndTheCutFrameCarriesNoColonyFacts()
    {
        var combat = FacadeNativeClient.DecodeCombat(View(ColonyFactsCoreTests.Snapshot() with { OutdoorTemperatureC = -31.5, ColonistCount = 4 }));
        Assert.Equal(new RimGovernor.Brain.Policy.OutdoorTemperature.Measured(-31.5), combat.OutdoorTemperature);
        // No partial colony facts whose other fields would read as zero.
        Assert.Null(combat.Frame!.ColonyFacts);
    }

    [Fact]
    public void AFrameWithoutColonyFactsNamesTheMissingColonyFactsNotZero()
    {
        var combat = FacadeNativeClient.DecodeCombat(View(null));
        Assert.IsType<RimGovernor.Brain.Policy.OutdoorTemperature.NoColonyFacts>(combat.OutdoorTemperature);
    }
}
