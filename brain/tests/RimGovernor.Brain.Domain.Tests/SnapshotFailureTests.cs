using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SnapshotFailureTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.Identity Identity = new() { ColonyId = "snapshot-test", LoadToken = "load", MapId = 0 };
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = Identity, Tick = 46189 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

#pragma warning disable CA1852 // DispatchProxy derives its implementation from this class.
    private class Game : DispatchProxy
    {
        internal FrameHandoff Handoff { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_" + nameof(IGameApi.Frames) => Handoff.Reader,
            nameof(IGameApi.OpenFramesAsync) => Task.FromResult(new RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply()),
            _ => throw new InvalidOperationException("Unexpected native call: " + targetMethod.Name),
        };
    }
#pragma warning restore CA1852

    [Fact]
    public async Task FailureIsReturnedWithoutWaitingAndNextFrameRecoversAtSameTick()
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        using var client = new GameClient(facade);
        await client.OpenFramesAsync(Ct);
        const string detail = "snapshot section Buildings: ReadFailed: building 42: Invalid native amount.";
        game.Handoff.Publish(new NativeFrame(new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot
        {
            Failure = new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure, Detail = detail },
        }, 122, 0));
        var refused = client.ReadBuildingsAsync(Identity, Ct);
        // The frame is already available: no clock, deadline or new frame should be needed.
        Assert.True(refused.IsCompleted);
        var error = await Assert.ThrowsAsync<ReadRefusedException>(() => refused);
        Assert.Contains(detail, error.Message, StringComparison.Ordinal);
        game.Handoff.Publish(new NativeFrame(new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot
        {
            Context = Context,
            Buildings = new RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot { Context = Context, Buildings = [] },
        }, 122, 0));
        var recovered = client.ReadBuildingsAsync(Identity, Ct);
        Assert.True(recovered.IsCompleted);
        Assert.Equal(Context, (await recovered).Context);
    }

    [Fact]
    public async Task MissingSectionIsNotReportedAsAnEmptyBuildingCensus()
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        using var client = new GameClient(facade);
        await client.OpenFramesAsync(Ct);
        game.Handoff.Publish(new NativeFrame(new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot { Context = Context }, 122, 0));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = client.ReadBuildingsAsync(Identity, cancel.Token);
        Assert.False(pending.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
