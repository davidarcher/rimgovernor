using RimGovernor.Brain.Host;
using RimGovernor.Brain.PlayerApi;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AutosaverTests
{
    private sealed class RecordingLifecycle(List<string> order, Func<string, bool> fail) : ILifecycleWriter
    {
        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken)
        {
            if (fail(request.SaveName ?? ""))
            {
                throw new InvalidOperationException("disk full");
            }
            order.Add("save:" + request.SaveName);
            return Task.FromResult(new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply());
        }

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> LoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> NewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> MenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly RimGovernor.Brain.GameFacts.Common.Identity World = new() { ColonyId = "c", LoadToken = "l", MapId = 1 };

    private static Autosaver Build(List<string> order, Func<string, bool>? fail = null)
    {
        var lifecycle = new FlushedLifecycle(new RecordingLifecycle(order, fail ?? (_ => false)), _ =>
        {
            order.Add("flush");
            return Task.CompletedTask;
        });
        return new Autosaver(lifecycle.SaveAsync, new HostLog(RequiredFactSink.Emitter(), "autosave"));
    }

    [Fact]
    public async Task EachAutosaveFlushesGovernorStateBeforeItSavesAndRotatesThroughFiveSlots()
    {
        var order = new List<string>();
        var saver = Build(order);
        for (var i = 0; i < 6; i++)
        {
            await saver.SaveAsync(World, 100 * i, CancellationToken.None);
        }

        Assert.Equal(
            ["flush", "save:Autosave-1", "flush", "save:Autosave-2", "flush", "save:Autosave-3", "flush", "save:Autosave-4", "flush", "save:Autosave-5", "flush", "save:Autosave-1"],
            order);
    }

    [Fact]
    public async Task AFailedAutosaveIsSwallowedAndKeepsItsSlotForTheNextHold()
    {
        var order = new List<string>();
        var failFirst = true;
        var saver = Build(order, name =>
        {
            var fail = failFirst && name == "Autosave-1";
            failFirst = false;
            return fail;
        });
        await saver.SaveAsync(World, 1, CancellationToken.None);
        await saver.SaveAsync(World, 2, CancellationToken.None);

        Assert.Equal(["flush", "flush", "save:Autosave-1"], order);
    }
}
