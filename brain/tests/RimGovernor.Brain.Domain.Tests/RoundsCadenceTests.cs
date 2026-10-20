using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The rounds cadence (#3556): between two rounds reviews a review step runs its dirty and due planners against the
/// last committed review; the rounder runs again once the review has stood
/// <see cref="ClockSchedulerConfig.DefaultRoundsEveryTicks"/>, on an early review (a wake), whenever every planner
/// runs, and once a colonist lost its activity Method (#3599).
/// </summary>
public sealed class RoundsCadenceTests
{
    private const long Every = ClockSchedulerConfig.DefaultRoundsEveryTicks;
    private const long Reviewed = 10_000;

    [Fact]
    public void AReviewStepBetweenTwoDueTicksReadsTheLastReview()
    {
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, false));
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every - 1, true, Reviewed, Every, false));
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed + 600, true, Reviewed, Every, false));
    }

    [Fact]
    public void TheRounderRunsOnceTheReviewHasStoodAnHour()
    {
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every, false));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed + Every + 600, true, Reviewed, Every, false));
    }

    [Fact]
    public void TheFirstReviewAndATickThatWentBackRunTheRounder()
    {
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, 600, false, 0, Every, false));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed - 1, true, Reviewed, Every, false));
    }

    [Fact]
    public void AnEarlyReviewAndAStepOfEveryPlannerReviewFirst()
    {
        Assert.Equal("wake", ClockScheduler.RoundsSelection(StepCause.Wake, false, Reviewed + 600, true, Reviewed, Every, false));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Full, true, Reviewed + 600, true, Reviewed, Every, false));
        Assert.Equal("all", ClockScheduler.RoundsSelection(StepCause.Review, true, Reviewed + 600, true, Reviewed, Every, false));
    }

    /// <summary>
    /// #3599: a review deferred three colonists for 600 ticks (their meal was far off); the deferral ran out and the
    /// worker released them at tick +600, and they stood idle until the hourly review at +3000, the throughput stage's
    /// whole run long. A colonist whose activity Method is gone is reassigned by the next review step.
    /// </summary>
    [Fact]
    public void AColonistFreedBetweenTwoReviewsRunsTheRounderAtTheNextReviewStep()
    {
        var deferred = new HashSet<string>(["Thing_Human185", "Thing_Human189"], StringComparer.Ordinal);
        Assert.True(ClockScheduler.PawnsFreed(deferred, new HashSet<string>(["Thing_Human189"], StringComparer.Ordinal)));
        Assert.Equal("freed", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 1200, true, Reviewed, Every, true));
        Assert.Equal("freed", ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed + 600, true, Reviewed, Every, true));
    }

    /// <summary>#3624: a coupled worker finished a task with bills left: the next step runs the rounder to reconsider who
    /// takes its next task, without a freed colonist or a due review; freed and due still name their own reasons.</summary>
    [Fact]
    public void AFinishedTaskRunsTheRounderAtTheNextStep()
    {
        Assert.Equal("task", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, false, true));
        Assert.Equal("freed", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, true, true));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every, false, true));
    }

    /// <summary>#3652: while the first layout plan holds the clock no tick advances, so a retry step between due ticks
    /// reviews again to read the fact the plan waits for; due still names its own reason.</summary>
    [Fact]
    public void AHeldFirstPlanRetriesTheReviewWhileTheClockIsHeld()
    {
        Assert.Equal("first_plan", ClockScheduler.RoundsSelection(StepCause.Retry, false, Reviewed, true, Reviewed, Every, false, false, true));
        Assert.Equal("first_plan", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + 600, true, Reviewed, Every, true, true, true));
        Assert.Equal("due", ClockScheduler.RoundsSelection(StepCause.Review, false, Reviewed + Every, true, Reviewed, Every, false, false, true));
    }

    /// <summary>
    /// #3599, the speedmatrix run: the review at +1200 deferred the colonists for 600 ticks; the deferral installed after
    /// that step read the journal and was released before the +1800 step read it, so neither read saw it held. The
    /// review's own assignment is what makes the +1800 step find them freed.
    /// </summary>
    [Fact]
    public void AColonistAReviewAssignedFreesItEvenIfNoStepSawItHeld()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(["Thing_Human185"], StringComparer.Ordinal);
        Assert.False(ClockScheduler.PawnsFreed(none, none));
        Assert.True(ClockScheduler.PawnsFreed(ClockScheduler.HeldAfterReview(none, assigned), none));
    }

    [Fact]
    public void AColonistStillHeldOrNewlyHeldFreesNothing()
    {
        var held = new HashSet<string>(["Thing_Human185"], StringComparer.Ordinal);
        Assert.False(ClockScheduler.PawnsFreed(held, new HashSet<string>(["Thing_Human185", "Thing_Human193"], StringComparer.Ordinal)));
        Assert.False(ClockScheduler.PawnsFreed(new HashSet<string>(StringComparer.Ordinal), held));
    }

    [Fact]
    public void TheCadenceIsOneInGameHourByDefault()
    {
        Assert.Equal(GameTime.TicksPerHour, Every);
        Assert.Equal(Every, new ClockSchedulerConfig().RoundsEveryTicks);
    }
}
