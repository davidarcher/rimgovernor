using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastSkillTests
{
    private static SkillLearning Evidence() => new()
    {
        Disabled = Known(false), TrackerAllowsLearning = Known(true), StoredLevel = Known(1), Aptitude = Known(0),
        Xp = Known(90d), XpSinceMidnight = Known(0d), DirectFactor = Known(1d), UnsaturatedFactor = Known(2d),
        SaturationThreshold = Known(40d), SaturatedFactor = Known(.2), LossThreshold = Known(-100d),
        DecayInterval = Known(200), NextDecay = Known(200), ValidForTicks = Known(1000),
        Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)],
        PracticeEligible = Known(true), PracticeCeiling = Known(2), RangeAppliedBudget = Known(30d),
    };

    [Fact]
    public void RangePaysPassionOnceAndIgnoresGlobalModifierAndSaturation()
    {
        var s = Evidence() with { DirectFactor = Known(1.5), XpSinceMidnight = Known(100d) };
        var range = ForecastSkillPolicy.ForecastSkill(s, [new("shot", 1, Known(10d), SkillXpPath.Range)], 100);
        var work = ForecastSkillPolicy.ForecastSkill(s, [new("work", 1, Known(10d), SkillXpPath.NormalTracker)], 100);
        Assert.Equal(15, range.AppliedGain.Value);
        Assert.Equal(4, work.AppliedGain.Value);
        var modified = ForecastSkillPolicy.ForecastSkill(s with { UnsaturatedFactor = Known(4d) }, [new("work", 1, Known(10d), SkillXpPath.NormalTracker)], 100);
        Assert.Equal(8, modified.AppliedGain.Value);
    }

    [Fact]
    public void PracticeAndProductiveWorkShareOneBudgetWithoutDuplicatingGains()
    {
        var result = ForecastSkillPolicy.ForecastSkill(Evidence(), [new("shot1", 1, Known(20d), SkillXpPath.Range),
            new("shot2", 2, Known(20d), SkillXpPath.Range), new("work", 3, Known(10d), SkillXpPath.NormalTracker)], 100);
        Assert.Equal(50, result.AppliedGain.Value);
        Assert.Equal(0, result.RangeBudget.Value);
        Assert.Equal(140, result.Xp.Value);
        var duplicate = new SkillXpEvent("same", 1, Known(10d), SkillXpPath.Range);
        Assert.False(ForecastSkillPolicy.ForecastSkill(Evidence(), [duplicate, duplicate], 100).Xp.IsKnown);
    }

    [Fact]
    public void IdleDecayAndActiveWorkCanReverseCapabilityThreshold()
    {
        var s = Evidence() with { Xp = Known(-95d) };
        Assert.Equal(0, ForecastSkillPolicy.ForecastSkill(s, [], 200).Level.Value);
        var active = ForecastSkillPolicy.ForecastSkill(s, [new("work", 1, Known(10d), SkillXpPath.NormalTracker)], 200);
        Assert.Equal(1, active.Level.Value);
        Assert.Equal(-85, active.Xp.Value);
        Assert.Equal(10, active.DecayLoss.Value);
    }

    [Fact]
    public void ThresholdCrossingUsesStoredLevelsAndAptitudeAndStopsPracticeAtCeiling()
    {
        var s = Evidence() with { Xp = Known(190d), RangeAppliedBudget = Known(100d) };
        var result = ForecastSkillPolicy.ForecastSkill(s, [new("first", 1, Known(15d), SkillXpPath.Range), new("second", 2, Known(15d), SkillXpPath.Range)], 100);
        Assert.Equal(2, result.Level.Value);
        Assert.Equal(5, result.Xp.Value);
        Assert.Equal(15, result.AppliedGain.Value);
        Assert.Equal(0, ForecastSkillPolicy.ForecastSkill(s with { Aptitude = Known(1) }, [new("shot", 1, Known(15d), SkillXpPath.Range)], 100).AppliedGain.Value);
    }

    [Fact]
    public void CompletionLearningRequiresCompletionAndSaturationUsesCallbackBoundaries()
    {
        var recipe = new RecipeLearning("Cooking", Known(.1), false);
        var interrupted = ForecastSkillPolicy.RecipeCompletionLearning(recipe, "recipe", 100, 100, false);
        var complete = ForecastSkillPolicy.RecipeCompletionLearning(recipe, "recipe", 100, 100, true);
        Assert.Equal(0, ForecastSkillPolicy.ForecastSkill(Evidence(), [interrupted], 100).AppliedGain.Value);
        Assert.Equal(20, ForecastSkillPolicy.ForecastSkill(Evidence(), [complete], 100).AppliedGain.Value);
        var s = Evidence() with { XpSinceMidnight = Known(40d) };
        Assert.Equal(24, ForecastSkillPolicy.ForecastSkill(s, [new("a", 1, Known(10d), SkillXpPath.NormalTracker),
            new("b", 2, Known(10d), SkillXpPath.NormalTracker)], 100).AppliedGain.Value);
        Assert.False(ForecastSkillPolicy.RecipeCompletionLearning(recipe with { DuringWork = true }, "uft", 100, 100, true).RawXp.IsKnown);
    }

    [Fact]
    public void DisabledZeroAndMissingMechanicsAreDifferent()
    {
        var e = new SkillXpEvent("work", 1, Known(10d), SkillXpPath.NormalTracker);
        Assert.Equal(0, ForecastSkillPolicy.ForecastSkill(Evidence() with { Disabled = Known(true) }, [e], 300).AppliedGain.Value);
        Assert.Equal(0, ForecastSkillPolicy.ForecastSkill(Evidence() with { TrackerAllowsLearning = Known(false) }, [e], 300).AppliedGain.Value);
        Assert.False(ForecastSkillPolicy.ForecastSkill(Evidence() with { DirectFactor = default }, [e], 100).Xp.IsKnown);
        Assert.False(ForecastSkillPolicy.ForecastSkill(Evidence(), [e with { RawXp = default }], 100).Xp.IsKnown);
        Assert.False(ForecastSkillPolicy.ForecastSkill(Evidence(), [], 1000).Xp.IsKnown);
    }
    [Fact]
    public void DecayChangingEligibilityRequiresFreshNativeEvidence()
    {
        var s = Evidence() with { StoredLevel = Known(2), Xp = Known(-95d), PracticeEligible = Known(false) };
        var result = ForecastSkillPolicy.ForecastSkill(s, [new("shot", 201, Known(10d), SkillXpPath.Range)], 300);
        Assert.False(result.Xp.IsKnown);
        Assert.Contains("practice_eligibility_requires_refresh", result.Findings);
        Assert.False(ForecastSkillPolicy.ForecastSkill(Evidence() with { ValidForTicks = Known(1) }, [], 1).Xp.IsKnown);
    }    [Fact]
    public void FiniteButOverflowingLearningIsNotKnownProgress()
    {
        var result = ForecastSkillPolicy.ForecastSkill(Evidence(), [new("bad", 1, Known(double.MaxValue), SkillXpPath.NormalRecord)], 100);
        Assert.False(result.Xp.IsKnown);
        Assert.Contains("learning_arithmetic_overflow", result.Findings);
    }    [Fact]
    public void NativeWireKeepsKnownZeroSeparateFromMissingEvidence()
    {
        var skill = new Skill { Learning = Evidence() with { DirectFactor = Known(0d), RangeAppliedBudget = default } };
        var info = RimGovernor.Contracts.Wire.WireJsonContext.Default.GetTypeInfo(typeof(Skill))!;
        var json = System.Text.Json.JsonSerializer.Serialize(skill, info);
        var copy = (Skill)System.Text.Json.JsonSerializer.Deserialize(json, info)!;
        Assert.NotNull(copy.Learning);
        Assert.True(copy.Learning.Disabled.IsKnown);
        Assert.False(copy.Learning.Disabled.Value);
        Assert.True(copy.Learning.DirectFactor.IsKnown);
        Assert.Equal(0, copy.Learning.DirectFactor.Value);
        Assert.False(copy.Learning.RangeAppliedBudget.IsKnown);
        Assert.Equal(skill.Learning.Levels, copy.Learning.Levels);
    }
}
