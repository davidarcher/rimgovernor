using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ObservationIdentityTotalTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new()
    {
        Identity = new() { ColonyId = "colony", MapId = 1, LoadToken = "load" },
        Tick = 200,
        NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(),
    };

    [Fact]
    public void BeforeTheFirstGrantGenerationIsUnclaimedAndPauseIsObserved()
    {
        var identity = new ObservationIdentity { Context = Context, Paused = true };
        Assert.Same(Context, identity.Context);
        Assert.IsType<RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase>(identity.NativeGeneration);
        Assert.True(identity.Paused);
        Assert.False(ColonyContexts.SameColonyContext(identity, identity));
    }

    [Fact]
    public void GrantedGenerationAndPauseAreTotal()
    {
        var identity = new ObservationIdentity
        {
            Context = Context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(7) },
            Paused = false,
        };
        Assert.IsType<RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase>(identity.NativeGeneration);
        Assert.True(identity.NativeGeneration.IsAt(7));
        Assert.False(identity.Paused);
        Assert.True(ColonyContexts.SameColonyContext(identity, identity));
    }

    [Fact]
    public void DirectConstructionRetainsTheNativeContextAndGenerationReferences()
    {
        var context = Context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(7) };
        var identity = new ObservationIdentity { Context = context, Paused = true };

        Assert.Same(context, identity.Context);
        Assert.Same(context.NativeGeneration, identity.NativeGeneration);
        Assert.Equal((new ColonyId("colony"), new LoadId("load"), new MapId(1), new Tick(200)),
            (identity.Colony, identity.Load, identity.Map, identity.Tick));
        Assert.True(identity.Paused);
    }

    public static TheoryData<string> InvalidIdentityText => ["", " ", "bad\0identity"];

    [Fact]
    public void UnpairedUtf16SurrogateFailsConstruction()
    {
        // Construct inside the test: serializing theory data can replace an unpaired surrogate before invocation.
        InvalidNativeIdentityTextFailsConstruction(new string((char)0xd800, 1));
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityText))]
    public void InvalidNativeIdentityTextFailsConstruction(string invalid)
    {
        var colony = Context with { Identity = Context.Identity with { ColonyId = invalid } };
        var load = Context with { Identity = Context.Identity with { LoadToken = invalid } };
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = colony });
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = load });
    }

    [Fact]
    public void NegativeNativeMapAndTickFailConstruction()
    {
        var map = Context with { Identity = Context.Identity with { MapId = -1 } };
        var tick = Context with { Tick = -1 };
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = map });
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = tick });
    }

    [Fact]
    public void MissingContextAndGenerationCannotBecomeAZeroGeneration()
    {
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = null! });
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = Context with { Identity = null! } });
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = Context with { NativeGeneration = null! } });
        Assert.Throws<ObservationContractException>(() => new ObservationIdentity { Context = Context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(0) } });
    }
}
