using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastSkillTests
{
    private static SkillLearning Evidence() => new()
    {
        Disabled = false, TrackerAllowsLearning = true, StoredLevel = 1, Aptitude = 0,
        Xp = 90d, XpSinceMidnight = 0d, DirectFactor = 1d, UnsaturatedFactor = 2d,
        SaturationThreshold = 40d, SaturatedFactor = .2, LossThreshold = -100d,
        DecayInterval = 200, NextDecay = 200, ValidForTicks = 1000, NextMidnightReset = 1000,
        TrackerIntervalTicks = 1, TrackerPhase = 0, TrackerElapsedTicks = 0,
        Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)],
        Practice = new SkillPractice.Range(true, true, 2, 30d, 30d),
    };

    /// <summary>The projection of a forecast the test expects projected.</summary>
    internal static SkillProjection Projected(SkillForecast forecast) => Assert.IsType<SkillForecast.Projected>(forecast).Projection;

    /// <summary>The findings of a forecast the test expects unprojectable.</summary>
    internal static EquatableArray<string> Unprojectable(SkillForecast forecast) => Assert.IsType<SkillForecast.Unprojectable>(forecast).Findings;

    [Fact]
    public void RangePaysPassionOnceAndIgnoresGlobalModifierAndSaturation()
    {
        var s = Evidence() with { DirectFactor = 1.5, XpSinceMidnight = 100d };
        var range = Projected(ForecastSkillPolicy.ForecastSkill(s, [new("shot", 1, 10d, SkillXpPath.Range)], 100));
        var work = Projected(ForecastSkillPolicy.ForecastSkill(s, [new("work", 1, 10d, SkillXpPath.NormalTracker)], 100));
        Assert.Equal(15, range.AppliedGain);
        Assert.Equal(4, work.AppliedGain);
        var modified = Projected(ForecastSkillPolicy.ForecastSkill(s with { UnsaturatedFactor = 4d }, [new("work", 1, 10d, SkillXpPath.NormalTracker)], 100));
        Assert.Equal(8, modified.AppliedGain);
    }

    [Fact]
    public void PracticeAndProductiveWorkShareOneBudgetWithoutDuplicatingGains()
    {
        var result = Projected(ForecastSkillPolicy.ForecastSkill(Evidence(), [new("shot1", 1, 20d, SkillXpPath.Range),
            new("shot2", 2, 20d, SkillXpPath.Range), new("work", 3, 10d, SkillXpPath.NormalTracker)], 100));
        Assert.Equal(50, result.AppliedGain);
        Assert.Equal(140, result.Xp);
        var duplicate = new SkillXpEvent("same", 1, 10d, SkillXpPath.Range);
        Assert.Contains("learning_events_invalid_duplicate_or_unordered",
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence(), [duplicate, duplicate], 100)));
    }

    [Fact]
    public void IdleDecayAndActiveWorkCanReverseCapabilityThreshold()
    {
        var s = Evidence() with { Xp = -95d };
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(s, [], 200)).Level);
        var active = Projected(ForecastSkillPolicy.ForecastSkill(s, [new("work", 1, 10d, SkillXpPath.NormalTracker)], 200));
        Assert.Equal(1, active.Level);
        Assert.Equal(-85, active.Xp);
        Assert.Equal(10, active.DecayLoss);
    }

    [Fact]
    public void ThresholdCrossingUsesStoredLevelsAndAptitudeAndStopsPracticeAtCeiling()
    {
        var s = Evidence() with { Xp = 190d, Practice = new SkillPractice.Range(true, true, 2, 100d, 100d) };
        var result = Projected(ForecastSkillPolicy.ForecastSkill(s, [new("first", 1, 15d, SkillXpPath.Range), new("second", 2, 15d, SkillXpPath.Range)], 100));
        Assert.Equal(2, result.Level);
        Assert.Equal(5, result.Xp);
        Assert.Equal(15, result.AppliedGain);
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(s with { Aptitude = 1 }, [new("shot", 1, 15d, SkillXpPath.Range)], 100)).AppliedGain);
    }

    [Fact]
    public void CompletionLearningRequiresCompletionAndSaturationUsesCallbackBoundaries()
    {
        var recipe = new RecipeLearning("Cooking", .1, false);
        var interrupted = ForecastSkillPolicy.RecipeCompletionLearning(recipe, "recipe", 100, 100, false);
        var complete = ForecastSkillPolicy.RecipeCompletionLearning(recipe, "recipe", 100, 100, true);
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(Evidence(), [interrupted], 100)).AppliedGain);
        Assert.Equal(20, Projected(ForecastSkillPolicy.ForecastSkill(Evidence(), [complete], 100)).AppliedGain);
        var s = Evidence() with { XpSinceMidnight = 40d };
        Assert.Equal(24, Projected(ForecastSkillPolicy.ForecastSkill(s, [new("a", 1, 10d, SkillXpPath.NormalTracker),
            new("b", 2, 10d, SkillXpPath.NormalTracker)], 100)).AppliedGain);
        Assert.Throws<ArgumentException>(() => ForecastSkillPolicy.RecipeCompletionLearning(recipe with { DuringWork = true }, "uft", 100, 100, true));
    }

    [Fact]
    public void DisabledZeroAndUnprojectableMechanicsAreDifferent()
    {
        var e = new SkillXpEvent("work", 1, 10d, SkillXpPath.NormalTracker);
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(Evidence() with { Disabled = true }, [e], 300)).AppliedGain);
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(Evidence() with { TrackerAllowsLearning = false }, [e], 300)).AppliedGain);
        Assert.Equal(0, Projected(ForecastSkillPolicy.ForecastSkill(Evidence() with { DirectFactor = 0d }, [new("shot", 1, 10d, SkillXpPath.Range)], 100)).AppliedGain);
        Assert.Equal(["learning_mechanics_unread"], Unprojectable(ForecastSkillPolicy.ForecastSkill(null, [e], 100)));
        Assert.Equal(["learning_mechanics_outside_supported_window"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence() with { DirectFactor = double.NaN }, [e], 100)));
        Assert.Equal(["learning_mechanics_outside_supported_window"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence() with { NextMidnightReset = -1 }, [e], 100)));
        Assert.Equal(["learning_events_invalid_duplicate_or_unordered"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence(), [e with { RawXp = double.NaN }], 100)));
        Assert.Equal(["learning_horizon_or_event_bound"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence(), [], GameTime.TicksPerDay + 1)));
    }

    [Fact]
    public void PracticeNeedsTheSkillsDrill()
    {
        var shot = new SkillXpEvent("shot", 1, 10d, SkillXpPath.Range);
        Assert.Equal(["practice_event_on_unpractised_skill"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence() with { Practice = new SkillPractice.None() }, [shot], 100)));
        Assert.Equal(["practice_event_on_unpractised_skill"], Unprojectable(ForecastSkillPolicy.ForecastSkill(
            Evidence() with { Practice = new SkillPractice.None() }, [new("bout", 1, 10d, SkillXpPath.NormalTracker, Practice: true)], 100)));
        Assert.Equal(["range_event_without_range_budget"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence() with { Practice = new SkillPractice.Sparring(true, true, 2) }, [shot], 100)));
        Assert.True(Projected(ForecastSkillPolicy.ForecastSkill(Evidence() with { Practice = new SkillPractice.Sparring(true, true, 2) },
            [new("bout", 1, 10d, SkillXpPath.NormalTracker, Practice: true)], 100)).AppliedGain > 0);
    }

    [Fact]
    public void DecayChangingEligibilityRequiresFreshNativeEvidence()
    {
        var s = Evidence() with { StoredLevel = 2, Xp = -95d, Practice = new SkillPractice.Range(true, false, 2, 30d, 30d) };
        var result = ForecastSkillPolicy.ForecastSkill(s, [new("shot", 201, 10d, SkillXpPath.Range)], 300);
        Assert.Contains("practice_eligibility_requires_refresh", Unprojectable(result));
        Assert.Equal(["learning_mechanics_outside_supported_window"],
            Unprojectable(ForecastSkillPolicy.ForecastSkill(Evidence() with { ValidForTicks = 1 }, [new("shot", 1, 10d, SkillXpPath.Range)], 100)));
    }

    [Fact]
    public void FiniteButOverflowingLearningIsNotProjected()
    {
        var result = ForecastSkillPolicy.ForecastSkill(Evidence(), [new("bad", 1, double.MaxValue, SkillXpPath.NormalRecord)], 100);
        Assert.Contains("learning_arithmetic_overflow", Unprojectable(result));
    }

    [Fact]
    public void TheMidnightResetIsAlwaysReadAndLiftsSaturation()
    {
        var saturated = Evidence() with { XpSinceMidnight = 100d, NextMidnightReset = 50 };
        var before = Projected(ForecastSkillPolicy.ForecastSkill(saturated, [new("work", 10, 10d, SkillXpPath.NormalTracker)], 100));
        var after = Projected(ForecastSkillPolicy.ForecastSkill(saturated, [new("work", 60, 10d, SkillXpPath.NormalTracker)], 100));
        Assert.True(after.AppliedGain > before.AppliedGain);
        Assert.Equal(20, after.AppliedGain);
    }

    [Fact]
    public void NativeWireRoundTripsTheLearningAndEveryPracticeState()
    {
        var info = RimGovernor.Contracts.Wire.WireJsonContext.Default.GetTypeInfo(typeof(Skill))!;
        foreach (var practice in new SkillPractice[] { new SkillPractice.None(), new SkillPractice.Sparring(true, false, 13), new SkillPractice.Range(false, true, 12, 4.5, 30d) })
        {
            var skill = new Skill { Learning = Evidence() with { DirectFactor = 0d, Practice = practice } };
            var json = System.Text.Json.JsonSerializer.Serialize(skill, info);
            var copy = (Skill)System.Text.Json.JsonSerializer.Deserialize(json, info)!;
            Assert.Equal(skill.Learning, copy.Learning);
        }
    }

    [Theory]
    [InlineData(205)]
    [InlineData(250)]
    public void RebasedLearningPreservesNominalHashPhaseBetweenTrackerCallbacks(int split)
    {
        var observed = Evidence() with
        {
            TrackerIntervalTicks = 15, TrackerPhase = 0, NextDecay = 200,
            NextMidnightReset = 1000, Xp = 100d
        };
        var continuous = Projected(ForecastSkillPolicy.ForecastSkill(observed, [], 605));
        var first = Projected(ForecastSkillPolicy.ForecastSkill(observed, [], split));
        var resumed = Projected(ForecastSkillPolicy.ForecastSkill(first.TerminalLearning, [], 605 - split));
        Assert.Equal(continuous.Xp, resumed.Xp);
        Assert.Equal(continuous.TerminalLearning, resumed.TerminalLearning);
        Assert.Equal(30, continuous.DecayLoss);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RebasePreservesPendingDecayWithRetainedCounterAfterCadenceChange(int split)
    {
        var observed = Evidence() with
        {
            TrackerIntervalTicks = 15, TrackerPhase = 7, TrackerElapsedTicks = 13,
            NextDecay = 1, NextMidnightReset = 1000, Xp = 100d
        };
        var continuous = Projected(ForecastSkillPolicy.ForecastSkill(observed, [], 415));
        var first = Projected(ForecastSkillPolicy.ForecastSkill(observed, [], split));
        var resumed = Projected(ForecastSkillPolicy.ForecastSkill(first.TerminalLearning, [], 415 - split));
        Assert.Equal(continuous.TerminalLearning, resumed.TerminalLearning);
        Assert.Equal(30, continuous.DecayLoss);
    }
}
