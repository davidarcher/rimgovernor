using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HostileCensusTests
{
    [Fact]
    public void QuietAndActiveAreSampledStatesButAwaitingEmergencyCannotBePricedAsQuiet()
    {
        Assert.IsType<HostileCensus.Quiet>(HostileCensus.Of(0));
        Assert.Equal(2, Assert.IsType<HostileCensus.Active>(HostileCensus.Of(2)).Count);
        Assert.Equal(0, new HostileCensus.Quiet().ThreatCount);
        Assert.Throws<RequiredFactException>(() => new HostileCensus.AwaitingEmergency().ThreatCount);
        Assert.Throws<DomainException>(() => HostileCensus.Of(-1));
        Assert.Throws<DomainException>(() => new HostileCensus.Active(0).ThreatCount);
    }

    [Fact]
    public void PendingPodsRemainActiveWithoutAPawnThreatRow()
    {
        var clear = new EmergencyDecision(true, []);
        Assert.IsType<HostileCensus.Quiet>(EmergencyNeedsRules.HostilesOf(clear, false));
        Assert.Equal(1, Assert.IsType<HostileCensus.Active>(EmergencyNeedsRules.HostilesOf(clear, true)).Count);
    }

    [Fact]
    public void AnIncompleteEmergencyIsRefusedInsteadOfProducingAQuietCensus()
    {
        var stale = new EmergencyDecision(false, [new(EmergencyReason.StaleFacts, new("missing"))]);
        Assert.Throws<RequiredFactException>(() => EmergencyNeedsRules.HostilesOf(stale, false));
    }

    [Fact]
    public void DangerCooldownUsesTheLastObservedThreatAfterTheCensusBecomesQuiet()
    {
        var last = new Tick(100);
        Assert.True(ShelterRules.DangerWindowOf(new HostileCensus.Active(1), last, false, new(100)));
        Assert.True(ShelterRules.DangerWindowOf(new HostileCensus.Quiet(), last, true, new(101)));
        Assert.False(ShelterRules.DangerWindowOf(new HostileCensus.Quiet(), last, true, new(100 + ShelterRules.DangerCooldown.Value)));
        Assert.Throws<RequiredFactException>(() => ShelterRules.DangerWindowOf(new HostileCensus.AwaitingEmergency(), last, true, new(101)));
    }
}
