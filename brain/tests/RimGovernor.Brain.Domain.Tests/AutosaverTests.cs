using RimGovernor.Brain.Host;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Telemetry;
using Xunit;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AutosaverTests
{
    private sealed class RecordingLifecycle(List<string> order, Func<string, bool> fail) : ILifecycleWriter
    {
        public Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken)
        {
            if (fail(request.SaveName ?? ""))
            {
                throw new InvalidOperationException("disk full");
            }
            order.Add("save:" + request.SaveName);
            return Task.FromResult(new PbLifecycle.SaveReply());
        }

        public Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.MenuReply> MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly PbCommon.Identity World = new() { ColonyId = "c", LoadToken = "l", MapId = 1 };

    private static Autosaver Build(List<string> order, Func<string, bool>? fail = null)
    {
        var lifecycle = new FlushedLifecycle(new RecordingLifecycle(order, fail ?? (_ => false)), _ =>
        {
            order.Add("flush");
            return Task.CompletedTask;
        });
        return new Autosaver(lifecycle.SaveAsync, new HostLog(new TelemetryEmitter(NullTelemetrySink.Instance), "autosave"));
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
