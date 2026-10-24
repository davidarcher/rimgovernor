using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A mood timeline is a named state (#3897): projected with its break state assessed against an observed threshold or
/// unassessed without one, or unprojected when it refuses its inputs. Each state reaches the readers as itself.
/// </summary>
public sealed class ForecastMoodResultTests
{
    private const double Day = GameTime.TicksPerDay;

    [Fact]
    public void AnObservedThresholdAssessesTheBreaksWithEveryShift()
    {
        var mood = ForecastMood.ForecastMoodTimeline(ForecastConditionalMoodTests.PawnProbe(), Day, [], breakShifts: [.1]);
        var breaks = mood.Assessed();
        Assert.Equal(LayoutMoodRules.MoodTierCount, breaks.TierTicks.Count);
        Assert.Equal(.1, Assert.Single(breaks.Shifted).Shift);
        Assert.DoesNotContain("mental_break_probability_unassessed", mood.Findings);
    }

    [Fact]
    public void WithoutAThresholdTheMoodIsProjectedAndItsBreaksUnassessed()
    {
        var pawn = ForecastConditionalMoodTests.PawnProbe();
        var mood = ForecastMood.ForecastMoodTimeline(pawn with { Mood = pawn.Mood! with { BreakThreshold = 0 } }, Day, [], breakShifts: [.1]);
        Assert.IsType<ForecastMoodBreaks.Unassessed>(mood.Projected().Breaks);
        Assert.Contains("mental_break_probability_unassessed", mood.Findings);
        // The break days of a mood change are unpriced, never read as none.
        Assert.Null(ForecastHousing.GainPerDay(10, mood, new MoodBreakModel(), .1, 1));
    }

    [Fact]
    public void ARefusedTimelineIsUnprojectedAndNamesWhy()
    {
        var mood = ForecastMood.ForecastMoodTimeline(ForecastConditionalMoodTests.PawnProbe(), Day, [],
            situational: [("Moved", 10, 0), ("Moved", 5, 100)]);
        Assert.Equal(["unsupported_situational_change_stacking"], Assert.IsType<ForecastMoodResult.Unprojected>(mood).Findings);
        Assert.Null(ForecastHousing.GainPerDay(10, mood, new MoodBreakModel(), .1, 1));
    }
}
