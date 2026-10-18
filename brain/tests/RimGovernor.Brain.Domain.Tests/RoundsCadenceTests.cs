using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The rounds cadence (#3556): between two rounds reviews a review step runs its dirty and due planners against the
/// last committed review; the rounder runs again once the review has stood
/// <see cref="ClockSchedulerConfig.DefaultRoundsEveryTicks"/>, on an early review (a wake), and whenever every planner
/// runs.
/// </summary>
public sealed class RoundsCadenceTests
{
    private const long Every = ClockSchedulerConfig.DefaultRoundsEveryTicks;
    private const long Reviewed = 10_000;

    [Fact]
    public void AReviewStepBetweenTwoDueTicksReadsTheLastReview()
    {
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every));
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every - 1, true, Reviewed, Every));
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed + 600, true, Reviewed, Every));
    }

    [Fact]
    public void TheRounderRunsOnceTheReviewHasStoodAnHour()
    {
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed + Every + 600, true, Reviewed, Every));
    }

    [Fact]
    public void TheFirstReviewAndATickThatWentBackRunTheRounder()
    {
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, 600, false, 0, Every));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed - 1, true, Reviewed, Every));
    }

    [Fact]
    public void AnEarlyReviewAndAStepOfEveryPlannerReviewFirst()
    {
        Assert.Equal("wake", ClockScheduler.RoundsSelection(StepCause.Wake, false, Reviewed + 600, true, Reviewed, Every));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Full, true, Reviewed + 600, true, Reviewed, Every));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Review, true, Reviewed + 600, true, Reviewed, Every));
    }

    [Fact]
    public void TheCadenceIsOneInGameHourByDefault()
    {
        Assert.Equal(GameTime.TicksPerHour, Every);
        Assert.Equal(Every, new ClockSchedulerConfig().RoundsEveryTicks);
    }
}
