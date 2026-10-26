using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PackedStockTests
{
#pragma warning disable CA1852 // DispatchProxy derives its implementation.
    private class Game : DispatchProxy
    {
        internal List<RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest> Calls { get; } = [];
        internal SuppliesSnapshot Snapshot { get; set; } = new() { Context = ColonyFactsCoreTests.Snapshot().Context, Stocks = [] };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IGameApi.ListSuppliesAsync)) throw new NotSupportedException(method?.Name);
            var request = (RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest)args![0]!;
            Calls.Add(request);
            return Task.FromResult(Snapshot with { Stocks = EquatableArray.From(Snapshot.Stocks.Where(row => request.Filter.DefNames.Count == 0 || request.Filter.DefNames.Contains(row.DefName))) });
        }
    }
#pragma warning restore CA1852

    private static ResourceStock Container(int index) => new()
    {
        DefName = $"InstalledContainer{index}", Units = 1, Stacks = 1, Spawned = 1, Ours = 1, OursUnforbidden = 1, Forbidden = 0, PlayerFaction = 1,
        Holders = [], WeaponItems = [], Items = [new StockItem
        {
            Id = $"outer-{index}", Cell = new(1, 1),
            Packed = new PackedBuilding { Id = $"inner-{index}", DefName = $"InstalledBed{index}", Quality = new PackedQuality.Unrated(), MarketValue = 100 },
        }],
    };

    [Theory]
    [InlineData(25)]
    [InlineData(97)]
    public async Task AllContainerDefinitionsShareOneReadAndEachInnerBuildingInstallsOnce(int count)
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        game.Snapshot = game.Snapshot with { Stocks = EquatableArray.From(Enumerable.Range(0, count).Select(Container)) };
        using var client = new GameClient(facade);
        var stock = new PackedStock(client, game.Snapshot.Context.Identity);
        var ct = TestContext.Current.CancellationToken;
        foreach (var index in Enumerable.Range(0, count))
        {
            var move = await stock.InstallAsync($"InstalledBed{index}", new(3, 4), Rotation.North, ct);
            Assert.NotNull(move);
            Assert.Equal($"inner-{index}", move.Thing);
            var action = DomainAction.NewMoveBuildingAction(new($"install-{index}"), move);
            var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.RelocateCase>(IntentActionRules.IntentAction("install", action).Intent).Value;
            Assert.Equal($"inner-{index}", intent.ThingId);
            var destination = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move>(intent.Mode);
            Assert.Equal(new Cell(3, 4), destination.Destination);
            Assert.Null(await stock.InstallAsync($"InstalledBed{index}", new(3, 4), Rotation.North, ct));
        }
        var request = Assert.Single(game.Calls);
        Assert.Equal(StockCategory.Packed, request.Filter.Category);
        Assert.Empty(request.Filter.DefNames);
        Assert.Equal(StockOwnership.Ours, request.Filter.Ownership);
        Assert.False(request.Filter.IncludeHeld);
    }

    [Fact]
    public async Task AClaimedPackedObjectWithoutItsInnerBuildingFailsTheRead()
    {
        var facade = DispatchProxy.Create<IGameApi, Game>();
        var game = (Game)(object)facade;
        var row = Container(0);
        game.Snapshot = game.Snapshot with { Stocks = [row with { Items = [row.Items[0] with { Packed = null }] }] };
        using var client = new GameClient(facade);
        await Assert.ThrowsAsync<BridgeContractException>(() => client.ReadPackedItemsAsync(game.Snapshot.Context.Identity, TestContext.Current.CancellationToken));
    }
}
