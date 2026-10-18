using RimGovernor.Brain.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A step cancelled by its call budget is retried, not counted toward the fault limit that suspends authority (#3435).</summary>
public sealed class SchedulerLoopRetryTests
{
    [Fact]
    public void CallBudgetCancellationIsRetriedNotFaulted()
    {
        Assert.True(SchedulerLoop.IsRetry(new TaskCanceledException("A task was canceled"), faulted: true));
        Assert.True(SchedulerLoop.IsRetry(new OperationCanceledException(), faulted: true));
    }

    [Fact]
    public void OtherFaultsAndStallsStayFaults()
    {
        Assert.False(SchedulerLoop.IsRetry(new InvalidOperationException("boom"), faulted: true));
        Assert.False(SchedulerLoop.IsRetry(new TimeoutException("stalled"), faulted: false));
        Assert.False(SchedulerLoop.IsRetry(new TaskCanceledException(), faulted: false));
    }

    [Fact]
    public void ASettledStepRepeatsAtOnceUnlessThePreviousRepeatWasAtTheSameTick()
    {
        // A combat stop at a later tick is the fight's next decision: its hold and its resume are two settled steps, and
        // the second at the same tick waits for the timer (#3550: every stop used to wait out the back-off).
        Assert.True(SchedulerLoop.SettledNow(settled: true, settledAt: null, tick: 0));
        Assert.True(SchedulerLoop.SettledNow(settled: true, settledAt: 0, tick: 101));
        Assert.False(SchedulerLoop.SettledNow(settled: true, settledAt: 101, tick: 101));
        Assert.False(SchedulerLoop.SettledNow(settled: true, settledAt: -1, tick: null));
        Assert.False(SchedulerLoop.SettledNow(settled: false, settledAt: null, tick: 5));
    }

    [Fact]
    public void ConsecutiveCancellationsReachTheLimitAndASuccessResetsThem()
    {
        var steps = new StepSupervisor(faultLimit: 5);
        for (var i = 1; i < StepSupervisor.CancelLimit; i++)
        {
            Assert.False(steps.Cancelled());
        }
        steps.Succeeded();
        for (var i = 1; i < StepSupervisor.CancelLimit; i++)
        {
            Assert.False(steps.Cancelled());
        }
        Assert.True(steps.Cancelled());
    }

    [Fact]
    public void AFaultEndsTheCancellationRun()
    {
        var steps = new StepSupervisor(faultLimit: 5);
        for (var i = 1; i < StepSupervisor.CancelLimit; i++)
        {
            Assert.False(steps.Cancelled());
        }
        Assert.False(steps.Faulted(stall: false));
        Assert.False(steps.Cancelled());
    }
}
