using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class StorageNativeTests
{
#pragma warning disable CA1852 // DispatchProxy derives its implementation from this class.
    private class Game : DispatchProxy
    {
        internal RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest? Request { get; private set; }
        internal bool Refuse { get; set; }
        internal RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot History { get; } = new() { CurrentHour = 200, Groups = [] };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IGameApi.ReadStorageOccupancyAsync))
                throw new NotSupportedException(targetMethod?.Name);
            Request = (RimGovernor.Brain.GameOrders.Observations.StorageOccupancyRequest)args![0]!;
            return Refuse ? Task.FromException<RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot>(new InvalidOperationException("storage census failed")) : Task.FromResult(History);
        }
    }
#pragma warning restore CA1852

    [Fact]
    public async Task CommonStorageSourceReadsNativeHistoryAndPreservesFailures()
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        using var client = new GameClient(facade);
        IReviewStorageSource source = client;
        var history = await ((IReviewStorageSource)client).ReadStorageOccupancyAsync(["Zone_b", "Zone_a"], 128, TestContext.Current.CancellationToken);
        Assert.Same(game.History, history);
        Assert.Equal(["Zone_a", "Zone_b"], game.Request!.Groups);
        Assert.Equal(128, game.Request.SinceHour);
        game.Refuse = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ((IReviewStorageSource)client).ReadStorageOccupancyAsync(["Zone_a"], 128, TestContext.Current.CancellationToken));
        Assert.Equal("storage census failed", error.Message);
    }
}
