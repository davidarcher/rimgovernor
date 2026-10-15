using RimGovernor.Brain.Simulation.CombatDispatch;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatDispatchTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task QualificationStopsQueuedTrialsButOrdinarySweepsRetainTheirBehavior(bool stop, int expectedCalls)
    {
        var calls = new List<int>();
        var error = await Assert.ThrowsAsync<DispatchException<int>>(() => CombatDispatcher.Dispatch<int>([1, 2, 3], 1, "test",
            (_, seed, _) =>
            {
                calls.Add(seed);
                if (seed == 1) throw new InvalidOperationException("failed qualification gate");
                return Task.FromResult(seed);
            }, CancellationToken.None, stopOnFailure: stop));
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Equal(3, error.Report.Runs.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task QualificationFailureCancelsAlreadyActivePeer()
    {
        var peerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var peerCancelled = false;
        await Assert.ThrowsAsync<DispatchException<int>>(() => CombatDispatcher.Dispatch<int>([1, 2, 3], 2, "test",
            async (_, seed, cancellation) =>
            {
                Interlocked.Increment(ref calls);
                if (seed == 1)
                {
                    await peerStarted.Task;
                    throw new InvalidOperationException("failed qualification gate");
                }
                peerStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { peerCancelled = true; throw; }
                return seed;
            }, CancellationToken.None, stopOnFailure: true));
        Assert.Equal(2, calls);
        Assert.True(peerCancelled);
    }
}
