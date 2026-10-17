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
    public void AnUnknownFamilyNameIsRefused()
        => Assert.Throws<FormatException>(() => BrainFamilies.FromEnvironment("work,nope"));
}
