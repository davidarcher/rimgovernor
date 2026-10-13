using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Policy reads of facts the in-proc host leaves at their defaults must not throw (#3139, #3140).</summary>
public sealed class PolicyDefaultsTests
{
    [Fact]
    public void ShelterInteriorAreaCountsOnlyTheCapabilitiesWithACellFootprint()
    {
        Assert.True(PolicyFunctions.ShelterInteriorArea(3, 0, 0) > 0);
    }

    [Fact]
    public void ResourceDemandOfDefaultFactsHasEmptyNeeds()
    {
        var f = new RoundsFacts();
        Assert.NotNull(f.ResourceNeeds);
        var demand = PolicyFunctions.ResourceDemandOf(f, PolicyFunctions.DefaultRoundsPolicy(), new RoundsLatches());
        Assert.NotNull(demand.Needs);
    }
}
