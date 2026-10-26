using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using JournalStore = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LifecycleFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public class Session : DispatchProxy
    {
        internal Exception? DisableFailure { get; set; }
        internal bool Closed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IPlayerSession.Disable):
                    if (DisableFailure is { } failure) { throw failure; }
                    return null;
                case nameof(IPlayerSession.State): return default(ControlState);
                case nameof(IPlayerSession.CloseAsync): Closed = true; return Task.CompletedTask;
                default: throw new InvalidOperationException(targetMethod.Name);
            }
        }
    }

    private sealed class Worlds : IWorldSource
    {
        public async Task<World> ReadWorldAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerLifetimeCancellationDrainsAndReportsDisableFailure(bool fails)
    {
        using var lifetime = new CancellationTokenSource();
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), Ct);
        var session = DispatchProxy.Create<IWorkerSession, Session>();
        var fake = (Session)(object)session;
        using var player = Player.Create(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), journal, session, new Worlds(), lifetime.Token);
        var failure = new IOException("disable failed during shutdown");
        fake.DisableFailure = fails ? failure : null;
        lifetime.Cancel();
        fake.DisableFailure = null;
        if (fails)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => player.CloseAsync(Ct)));
        }
        else
        {
            await player.CloseAsync(Ct);
        }
        Assert.True(fake.Closed);
    }

    [Fact]
    public async Task WorkerCancellationRetainsItsStopFailureUntilCloseAndStillDrainsPlayer()
    {
        using var lifetime = new CancellationTokenSource();
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), Ct);
        var session = DispatchProxy.Create<IWorkerSession, Session>();
        var fake = (Session)(object)session;
        using var player = Player.Create(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), journal, session, new Worlds(), Ct);
        var worker = Worker.CreateInternal(new() { StepInterval = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1), StepTimeout = TimeSpan.FromSeconds(5) }, player, session, lifetime.Token);
        var failure = new IOException("worker disable failed");
        fake.DisableFailure = failure;
        lifetime.Cancel();
        fake.DisableFailure = null;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => worker.Close(Ct)));
        Assert.True(fake.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlFailureDistinguishesMissingRecordFromFailedJournalLookup(bool lookupFails)
    {
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), Ct);
        var session = DispatchProxy.Create<IWorkerSession, Session>();
        using var player = Player.Create(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), journal, session, new Worlds(), Ct);
        await player.CloseAsync(Ct);
        if (lookupFails) { journal.Dispose(); }
        var control = new HostPlayerControl(player, journal, new StepSupervisor(5), new HostLog(RequiredFactSink.Emitter(), "player"));
        var request = new ControlRequest("closed-player", ControlKind.Resume, new(new("colony"), new("load"), new(1)));
        Exception primary;
        if (lookupFails)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => control.ResumeAsync(request, Ct));
            Assert.Equal(2, failure.InnerExceptions.Count);
            primary = failure.InnerExceptions[0];
        }
        else
        {
            primary = await Assert.ThrowsAsync<ControlException>(() => control.ResumeAsync(request, Ct));
        }
        Assert.IsType<ControlException>(primary);
        Assert.Contains("p.closed", primary.Message, StringComparison.Ordinal);
    }
}
