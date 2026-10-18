using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3539: the three storyteller facts the native threat read adds to ThreatFacts reach the colony threat projection, and a
// field the native side withheld (absent, empty or non-finite) stays unknown, never zero.
public sealed class ThreatFactsTests
{
    [Fact]
    public void TheStorytellerDaysPassedAndAdaptationDaysProject()
    {
        var threat = ColonyThreat.ProjectColonyThreat(Census(new ThreatFacts { Storyteller = "Cassandra", DaysPassed = 12.5, AdaptationDays = -3 }));

        Assert.Equal("Cassandra", threat.Storyteller.Value);
        Assert.Equal(12.5, threat.DaysPassed.Value);
        Assert.Equal(-3, threat.AdaptationDays.Value);
    }

    [Fact]
    public void AWithheldFieldIsUnknown()
    {
        var threat = ColonyThreat.ProjectColonyThreat(Census(new ThreatFacts { Storyteller = "", DaysPassed = double.NaN }));

        Assert.False(threat.Storyteller.IsKnown);
        Assert.False(threat.DaysPassed.IsKnown);
        Assert.False(threat.AdaptationDays.IsKnown);
        Assert.False(ColonyThreat.ProjectColonyThreat(new ColonyFactsSnapshot()).Storyteller.IsKnown);
    }

    private static ColonyFactsSnapshot Census(ThreatFacts facts) => new() { Threat = new ThreatSection { Observed = facts } };
}
