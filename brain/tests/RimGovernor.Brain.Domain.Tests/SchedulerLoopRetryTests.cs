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
}
