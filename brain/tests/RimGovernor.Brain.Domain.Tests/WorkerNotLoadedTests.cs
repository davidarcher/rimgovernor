using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Store;
using JournalStore = RimGovernor.Brain.Store.Store;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The brain starts at the game's main menu, so the worker steps before any colony map exists. That read comes
/// back unavailable (NotLoaded); it must skip the step quietly instead of ending the step span with an exception
/// on every pass. Any other unavailable cause still surfaces.
/// </summary>
public sealed class WorkerNotLoadedTests
{
    private sealed class Worlds(Exception failure) : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromException<World>(failure);
    }

    private sealed class Session : IWorkerSession
    {
        public int Disabled;
        public int Batches;

        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => throw new NotSupportedException();
        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;
        public bool HoldsGrant(GenerationSnapshot scope) => false;
        public bool TargetsWorld(World world) => false;
        public void Disable() => Disabled++;
        public ControlState State() => default;
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Executor.BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
        {
            Batches++;
            return Task.FromResult<IReadOnlyList<Executor.BatchItem>>([]);
        }

        public Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<(Worker Worker, Session Session, CancellationTokenSource Lifetime, string Path)> Start(Exception failure)
    {
        var lifetime = new CancellationTokenSource();
        var path = Path.Combine(Path.GetTempPath(), "rg-worker-notloaded-" + Guid.NewGuid().ToString("N") + ".db");
        var journal = JournalStore.Open(path, lifetime.Token);
        var session = new Session();
        var player = Player.Create(new PlayerConfig(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), journal, session, new Worlds(failure), lifetime.Token);
        var config = new WorkerConfig { StepInterval = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1), StepTimeout = TimeSpan.FromSeconds(5) };
        var worker = Worker.CreateInternal(config, player, session, lifetime.Token);
        await Task.Yield();
        return (worker, session, lifetime, path);
    }

    [Fact]
    public async Task NotLoadedReadSkipsTheStepWithoutAnException()
    {
        var (worker, session, lifetime, _) = await Start(new NotLoadedException("ReadTick: native read unavailable: NotLoaded: No current colony map is loaded."));
        try
        {
            await worker.Step(DateTimeOffset.UtcNow, lifetime.Token);
            Assert.Equal(0, session.Batches);
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    [Fact]
    public async Task OtherUnavailableCausesStillThrow()
    {
        var (worker, _, lifetime, _) = await Start(new BridgeUnavailableException("ReadTick: native read unavailable: NotObserved"));
        try
        {
            await Assert.ThrowsAsync<BridgeUnavailableException>(() => worker.Step(DateTimeOffset.UtcNow, lifetime.Token));
        }
        finally
        {
            lifetime.Cancel();
        }
    }
}
