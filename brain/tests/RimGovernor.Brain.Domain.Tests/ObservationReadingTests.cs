using RimGovernor.Brain.GameAccess;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ObservationReadingTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneNativeReadRetainsItsContextPauseAndMeasuredInterval()
    {
        var context = new RimGovernor.Brain.GameFacts.Common.ObservationContext
        {
            Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 1 },
            Tick = 42, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(),
        };
        var source = new Source(new() { Context = context, Paused = true, ForcePaused = false });
        var observed = Started.AddMilliseconds(10);
        var reading = await Bridge.ObservationReads.Observe(source, new Clock(Started, observed), TestContext.Current.CancellationToken);

        Assert.Equal(1, source.Calls);
        Assert.Same(context, reading.Identity.Context);
        Assert.True(reading.Identity.Paused);
        Assert.Equal((Started, observed), (reading.StartedAt, reading.ObservedAt));
        reading.CheckFresh(observed, TimeSpan.FromSeconds(1));
        Assert.Throws<ObservationStaleException>(() => reading.CheckFresh(Started, TimeSpan.FromSeconds(1)));
        Assert.Throws<ObservationStaleException>(() => reading.CheckFresh(observed.AddSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AnAbsentNativeContextFailsTheRead()
    {
        var source = new Source(new() { Context = null, Paused = false, ForcePaused = false });
        await Assert.ThrowsAsync<ObservationContractException>(() =>
            Bridge.ObservationReads.Observe(source, new Clock(Started, Started), TestContext.Current.CancellationToken));
    }

    private sealed class Source(RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick reply) : IObservationSource
    {
        public int Calls { get; private set; }
        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> TickAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private sealed class Clock(DateTimeOffset first, DateTimeOffset second) : IClock
    {
        private int reads;
        public DateTimeOffset Now() => reads++ == 0 ? first : second;
    }
}
