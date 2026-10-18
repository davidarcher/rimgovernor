using RimGovernor.Brain.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BrainFamiliesTests
{
    [Fact]
    public void ASelectionNarrowsTheComposedFamiliesToExactlyThoseNamed()
        => Assert.Equal(["work"], BrainFamilies.FromEnvironment("work").Active());

    [Fact]
    public void NoSelectionComposesEveryFamily()
    {
        Assert.Equal(BrainFamilies.All.Active(), BrainFamilies.FromEnvironment(null).Active());
        Assert.Equal(BrainFamilies.All.Active(), BrainFamilies.FromEnvironment(" ").Active());
    }

    [Fact]
    public void TheGoalsFamilyIsOnByDefaultAndNamedByItsToggle()
    {
        Assert.Contains("goals", BrainFamilies.All.Active());
        Assert.Equal(["goals"], BrainFamilies.FromEnvironment("goals").Active());
        Assert.DoesNotContain("goals", (BrainFamilies.All with { Goals = false }).Active());
        Assert.False(BrainFamilies.FromEnvironment("work").Goals);
    }

    [Fact]
    public void AnUnknownFamilyNameIsRefused()
        => Assert.Throws<FormatException>(() => BrainFamilies.FromEnvironment("work,nope"));
}
