using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The served wait (#3398): while the brain service owns the clock, the harness observes. The wait ends when the probe
/// is done and returns the game ticks that passed, fails once the game runs past its tick budget (also when the probe
/// finishes only after it), and ends at once when the service exits.
/// </summary>
public sealed class ServedWaitTests
{
    // A game the "service" advances 100 ticks between readings.
    private sealed class Clock
    {
        public ulong Tick { get; private set; } = 5_000;

        public Task<ulong> ReadAsync(CancellationToken cancellationToken)
        {
            var now = Tick;
            Tick += 100;
            return Task.FromResult(now);
        }
    }

    private static readonly Wait Fast = new() { Interval = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task EndsWhenTheProbeIsDoneAndReturnsTheTicksThatPassed()
    {
        var clock = new Clock();

        var ran = await ServedWait.UntilAsync("served", 1_000, clock.ReadAsync, _ => Task.FromResult(("", clock.Tick > 5_300)), Fast, CancellationToken.None);

        Assert.Equal(300, ran);
    }

    [Fact]
    public async Task FailsOnceTheGameRunsPastTheBudget()
    {
        var clock = new Clock();

        var e = await Assert.ThrowsAsync<AcceptanceException>(() => ServedWait.UntilAsync("served", 250, clock.ReadAsync, _ => Task.FromResult(("", false)), Fast, CancellationToken.None));

        Assert.StartsWith("served: wait tick budget spent: 300 ticks", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsAProbeDoneOnlyPastTheBudget()
    {
        var clock = new Clock();

        var e = await Assert.ThrowsAsync<AcceptanceException>(() => ServedWait.UntilAsync("served", 150, clock.ReadAsync, _ => Task.FromResult(("", clock.Tick > 5_200)), Fast, CancellationToken.None));

        Assert.Equal("served: done only after 200 ticks, past the wait's 150", e.Message);
    }

    [Fact]
    public async Task EndsAtOnceWhenTheServiceExits()
    {
        var clock = new Clock();
        var probes = 0;
        var wait = Fast with { Terminal = () => probes > 0 ? new AcceptanceException("service 7 exited") : null };

        var e = await Assert.ThrowsAsync<AcceptanceException>(() => ServedWait.UntilAsync("served", 1_000, clock.ReadAsync, _ => Task.FromResult(($"{++probes}", false)), wait, CancellationToken.None));

        Assert.Contains("service 7 exited", e.Message, StringComparison.Ordinal);
        Assert.Equal(1, probes);
    }
}
