using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The rounds cadence (#3556): between two rounds reviews a review step runs its dirty and due planners against the
/// last committed review, and so does a wake (an early review of every planner, #3726); the rounder runs again once
/// the review has stood <see cref="ClockSchedulerConfig.DefaultRoundsEveryTicks"/>, whenever every planner runs for a
/// full step or an authority change, once a coupled worker finished a task (#3624) and while the first plan holds the
/// clock (#3652).
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

    /// <summary>#3726: a wake between two due ticks runs its planners against the last review like a review step; it
    /// reviews when due, and a step of every planner reviews first.</summary>
    [Fact]
    public void AWakeReadsTheLastReviewAndAStepOfEveryPlannerReviewsFirst()
    {
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Wake, false, Reviewed + 600, true, Reviewed, Every));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Wake, false, Reviewed + Every, true, Reviewed, Every));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Wake, true, Reviewed + 600, true, Reviewed, Every));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Full, true, Reviewed + 600, true, Reviewed, Every));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Review, true, Reviewed + 600, true, Reviewed, Every));
    }

    /// <summary>#3624: a coupled worker finished a task with bills left: the next step runs the rounder to reconsider who
    /// takes its next task, without a due review; due still names its own reason.</summary>
    [Fact]
    public void AFinishedTaskRunsTheRounderAtTheNextStep()
    {
        Assert.Equal("task", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, true));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every, true));
    }

    /// <summary>#3652: while the first layout plan holds the clock no tick advances, so a retry step between due ticks
    /// reviews again to read the fact the plan waits for; due still names its own reason.</summary>
    [Fact]
    public void AHeldFirstPlanRetriesTheReviewWhileTheClockIsHeld()
    {
        Assert.Equal("first_plan", ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed, true, Reviewed, Every, false, true));
        Assert.Equal("first_plan", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, true, true));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every, false, true));
    }

    [Fact]
    public void TheCadenceIsOneInGameHourByDefault()
    {
        Assert.Equal(GameTime.TicksPerHour, Every);
        Assert.Equal(Every, new ClockSchedulerConfig().RoundsEveryTicks);
    }
}
