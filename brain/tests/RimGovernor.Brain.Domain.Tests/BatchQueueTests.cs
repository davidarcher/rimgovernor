using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BatchQueueTests
{
    // #3552: the flush interval bounds how long an item waits in the batch. A row stream that never pauses for a whole
    // interval used to keep resetting the idle timer, so a sweep's combat rows reached the ring only when 512 had queued
    // behind them.
    [Fact]
    public async Task SteadyTrickleExportsOnTheFlushIntervalNotWhenTheBatchFills()
    {
        var exported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stats = new DropStats("log", "test", TimeSpan.FromSeconds(1), null, TimeProvider.System);
        var queue = new BatchQueue<int>(stats, 16384, 512, TimeSpan.FromMilliseconds(100), null, (_, _) => exported.TrySetResult(), TimeProvider.System);

        var offered = 0;
        while (!exported.Task.IsCompleted && offered < 400)
        {
            Assert.True(queue.Offer(offered++));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(exported.Task.IsCompleted, $"{offered} items offered 10 ms apart and none exported past the 100 ms flush interval");
        await queue.ShutdownAsync(CancellationToken.None);
    }
}
