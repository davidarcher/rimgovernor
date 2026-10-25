using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalTraitTests
{
    [Fact]
    public void TraitEffectsKeepNativeIdentityAndExistingJournalPayload()
    {
        var observed = new O.Trait { DefName = "Industriousness", Degree = 2 };
        var trait = new PawnTrait { Observed = observed, Effects = new() { WorkSpeed = 0.35 } };
        var json = StoreJson.Serialize(trait);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["Name", "Degree", "Effects"], document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(observed.DefName, document.RootElement.GetProperty("Name").GetString());
        Assert.Equal(observed.Degree, document.RootElement.GetProperty("Degree").GetInt32());
        var restored = StoreJson.Deserialize<PawnTrait>(json);
        Assert.Equal(observed, restored.Observed);
        Assert.Equal(trait.Effects.DisabledWork, restored.Effects.DisabledWork);
        Assert.Equal(trait.Effects with { DisabledWork = restored.Effects.DisabledWork }, restored.Effects);
        Assert.Same(observed, trait.Observed);
    }
}