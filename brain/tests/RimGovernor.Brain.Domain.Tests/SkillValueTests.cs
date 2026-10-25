using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3912: a skill is worth the work its next level speeds up. The pawn's work added at the speeds one more level gives it, less its work
/// added now, against the colony's shortfall, over the days the level lasts and the XP it takes.
/// </summary>
public sealed class SkillValueTests
{
    private const double Day = GameTime.TicksPerDay;

    private static readonly PawnUpkeepRead Upkeep = NeedsFixtures.Upkeep with
    {
        Speeds = [new("ConstructionSpeed", 1.0), new("CookSpeed", 1.0)],
        SkillSpeeds = [new("Construction", "ConstructionSpeed", 1.2), new("Cooking", "CookSpeed", 1.5)],
    };

    private static WorkShortfall Short(double constructionTicks) =>
        new() { Rows = [new("Construction", "ConstructionSpeed", constructionTicks)] };

    private static double Of(string skill, WorkShortfall shortfall, double free = 20000, double days = 30, double xp = 1000, double horizon = 30) =>
        SkillValue.WorkPerXp("pawn", skill, Upkeep, new SkillRaise(xp, days), free, shortfall, horizon);

    [Fact]
    public void ASkillUsedByAWorkTypeTheColonyIsShortOfIsWorthItsSpeedGainOverTheXpTheLevelTakes()
    {
        // Free 20000 ticks at speed 1 fill 20000 of the 60000 short; at 1.2 they fill 24000: 4000 ticks a day, 4000 / a day capability-days
        // a day for 30 days over 1000 XP.
        Assert.Equal(4000 / Day * 30 / 1000, Of("Construction", Short(60000)), 12);
        Assert.True(Of("Construction", Short(60000)) > 0);
    }

    [Fact]
    public void ASkillNoNeededWorkUsesIsWorthNothing()
    {
        // The colony is short of construction only: a faster cook adds no work it needs.
        Assert.Equal(0, Of("Cooking", Short(60000)), 12);
        // A skill no speed reads has no row at all.
        Assert.Equal(0, Of("Shooting", Short(60000)), 12);
    }

    [Fact]
    public void ASpeedGainTheShortfallAlreadyFitsAddsNothing()
    {
        // The pawn's free time already fills the whole shortfall: working faster frees time, but nothing more is needed.
        Assert.Equal(0, Of("Construction", Short(10000)), 12);
        Assert.Equal(0, Of("Construction", WorkShortfall.None), 12);
    }

    [Fact]
    public void ALevelDecayTakesBackIsWorthItsDaysOnly()
    {
        Assert.Equal(Of("Construction", Short(60000)) / 3, Of("Construction", Short(60000), days: 10), 12);
        Assert.Equal(Of("Construction", Short(60000)), Of("Construction", Short(60000), days: 100), 12);
        Assert.Equal(0, Of("Construction", Short(60000), days: 0), 12);
    }

    [Fact]
    public void AStockAtTheTopLevelHasNoLevelToGain()
    {
        Assert.Equal(0, Of("Construction", Short(60000), xp: 0), 12);
    }

    internal static ChildWorkRead Childhood(double years = 8, bool disabled = false) => new(years, 4, 1, 1,
    [
        new((int)years, [new("ConstructionSpeed", 0.25)], [new("Construction", "ConstructionSpeed", 0.3)], ["Construction"]),
        new(10, [new("ConstructionSpeed", 1)], [new("Construction", "ConstructionSpeed", 1.2)], disabled ? ["Construction"] : []),
        new(11, [new("ConstructionSpeed", 2)], [new("Construction", "ConstructionSpeed", 2.4)], disabled ? ["Construction"] : []),
    ]);

    private static double ChildValue(ChildWorkRead child, double lasts = 60, double horizon = 60) =>
        SkillValue.WorkPerXp("child", "Construction", Upkeep with { ChildWork = child, IncapableWork = ["Construction"] },
            new(1000, lasts), 20000, Short(60000), horizon);

    [Fact]
    public void AChildEarnsFutureWorkOnlyAfterNativeAgeUnlocksItAtThatAgesSpeeds()
    {
        // Native child rate 4: age 10 in 30 game days and age 11 in 45. Work gains 4000/day, then 8000/day.
        Assert.Equal((4000 * 15 + 8000 * 15) / Day / 1000, ChildValue(Childhood()), 12);
        Assert.Equal(4000 * 10 / Day / 1000, ChildValue(Childhood(), horizon: 40), 12);
    }

    [Fact]
    public void AChildTooYoungWithinTheInvestmentHorizonEarnsNoFutureWork()
    {
        Assert.Equal(0, ChildValue(Childhood(years: 3)), 12);
        Assert.Equal(0, ChildValue(Childhood(), horizon: 30), 12);
        Assert.Equal(0, ChildValue(Childhood() with { GeneAgingFactor = 0 }), 12);
    }

    [Fact]
    public void AgingPreservesPermanentDisabilityAndCannotRecoverADecayedRaise()
    {
        Assert.Equal(0, ChildValue(Childhood(disabled: true)), 12);
        Assert.Equal(0, ChildValue(Childhood(), lasts: 20), 12);
        Assert.Equal(4000 * 5 / Day / 1000, ChildValue(Childhood(), lasts: 35), 12);
    }

    [Fact]
    public void NativeAgingInterpolatesChildAndAdultRatesAndAppliesTheObservedGeneFactor()
    {
        var child = Childhood() with { BiologicalYears = 11 };
        var expected = 60 * Math.Log((4d - 1d / 3) / 4) / (-1d / 3);
        Assert.Equal(expected, ChildWorkRules.DayAt(child, 12), 10);
        Assert.Equal(expected / 2, ChildWorkRules.DayAt(child with { GeneAgingFactor = 2 }, 12), 10);
        Assert.Equal(60, ChildWorkRules.DayAt(child with { BiologicalYears = 20 }, 21), 10);
        Assert.Throws<RequiredFactException>(() => ChildValue(Childhood() with { Ages = [] }));
    }

    [Fact]
    public void ChildWorkStatesRoundTripThroughTheCanonicalFactsCodecAndMissingReadsFail()
    {
        var read = Upkeep with { ChildWork = Childhood() };
        var json = JsonSerializer.Serialize(read, FactsJsonContext.Default.PawnUpkeepRead);
        Assert.Equal(read, JsonSerializer.Deserialize(json, FactsJsonContext.Default.PawnUpkeepRead));
        var absent = JsonNode.Parse(json)!.AsObject();
        absent.Remove("childWork");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(absent.ToJsonString(), FactsJsonContext.Default.PawnUpkeepRead));
    }

    [Fact]
    public void ASkillStockRaisedLastsUntilDecayTakesTheLevelBack()
    {
        ModelPawn Of(SkillLearning learning) => new() { Pawn = "pawn", Skills = EquatableArray.From<RimGovernor.Brain.GameFacts.Observations.Skill>([new() { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Shooting", Level = 1, Learning = learning }]), Consciousness = 1, Moving = 1, Manipulation = 1, Sight = 1, Talking = 1, ComfortableMin = 16, ComfortableMax = 26, ResearchSpeed = 1, PlantWorkSpeed = 1, Hunts = false };
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        // Level 1 of 0-2 at -95 XP, 5 over the loss threshold: the raised level (the top) decays 6000 XP a day and is gone within the hour.
        var lasts = Assert.Single(SkillStock.Of(Of(CapabilityMaintenanceTests.Learning()), findings)).RaisedLasts(300);
        Assert.InRange(lasts, 0, 0.01);
        // At the top level there is no level to raise.
        Assert.Equal(0, Assert.Single(SkillStock.Of(Of(CapabilityMaintenanceTests.Learning() with { StoredLevel = 2 }), findings)).RaisedLasts(300));
        // Without decay the raise lasts the whole horizon.
        Assert.Equal(300, Assert.Single(SkillStock.Of(Of(CapabilityMaintenanceTests.Learning() with { TrackerAllowsLearning = false }), findings)).RaisedLasts(300));
    }
}
