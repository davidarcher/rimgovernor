using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3927: the herd's precept bars read the Ideology mode a frame always states, with no unread case.</summary>
public sealed class HerdPreceptsTests
{
    private const string Slaughter = "SlaughteredAnimal";
    private const string VeneratedSlaughter = "SlaughteredVeneratedAnimal";
    private const string EatMeat = "AteMeat";

    private static readonly UpkeepAnimal Cow = new() { Id = new("cow"), Definition = new("Cow") };
    private static readonly UpkeepAnimal Muffalo = new() { Id = new("muffalo"), Definition = new("Muffalo"), Herd = new HerdFacts { Venerated = true } };

    /// <summary>An ideoligion holding one precept whose members refuse each of the events.</summary>
    private static IdeologyMode.Installed Refusing(params string[] events) => new IdeologyMode.Installed(new Ideoligion(
        new IdeologyDefs
        {
            Precepts = new Dictionary<string, PreceptDef>
            {
                ["Precept_Refuses"] = new(events.Select(e => new PreceptEffect { Kind = PreceptEffectKind.Unwilling, HistoryEvent = e }).ToArray()),
            },
        },
        new IdeoligionFacts { Development = IdeoDevelopment.Fixed, Precepts = [new HeldPrecept("p1", "Precept_Refuses")] }));

    [Fact]
    public void WithoutTheExpansionNothingIsBarred()
    {
        var barred = HerdPreceptsRules.ApplyHerdPrecepts(EquatableArray.From([Cow, Muffalo]), new IdeologyMode.NotInstalled());

        Assert.All(barred, a => Assert.False(a.Herd.SlaughterBarred || a.Herd.EatingBarred));
    }

    [Fact]
    public void AnIdeoligionThatRefusesSlaughterBarsSlaughterButNotEating()
    {
        var barred = HerdPreceptsRules.ApplyHerdPrecepts(EquatableArray.From([Cow]), Refusing(Slaughter));

        Assert.True(barred[0].Herd.SlaughterBarred);
        Assert.False(barred[0].Herd.EatingBarred);
    }

    [Fact]
    public void AVeneratedRacesExtraEventBarsOnlyTheVeneratedAnimal()
    {
        var barred = HerdPreceptsRules.ApplyHerdPrecepts(EquatableArray.From([Cow, Muffalo]), Refusing(VeneratedSlaughter, EatMeat));

        Assert.False(barred[0].Herd.SlaughterBarred);
        Assert.True(barred[1].Herd.SlaughterBarred);
        Assert.True(barred[0].Herd.EatingBarred);
        Assert.True(barred[1].Herd.EatingBarred);
    }

    [Fact]
    public void AnEmptyHerdStaysEmpty()
    {
        Assert.Empty(HerdPreceptsRules.ApplyHerdPrecepts(default, Refusing(Slaughter)));
    }

    [Fact]
    public void TheModeAllowsEveryActionWithoutTheExpansionAndAnswersFromThePrimaryWithIt()
    {
        var action = new PreceptAction { HistoryEvent = Slaughter };

        Assert.Equal(PreceptStance.Allowed, new IdeologyMode.NotInstalled().ActionStance(action, new PreceptSubject()).Stance);
        Assert.Equal(PreceptStance.Forbidden, Refusing(Slaughter).ActionStance(action, new PreceptSubject()).Stance);
        Assert.Equal(PreceptStance.Allowed, Refusing(EatMeat).ActionStance(action, new PreceptSubject()).Stance);
    }
}
