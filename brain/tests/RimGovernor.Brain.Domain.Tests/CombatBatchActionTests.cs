using System.Text.Json;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Contracts.Wire;
using Xunit;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatBatchActionTests
{
    private static DomainAction Batch(params CombatCommand[] orders) =>
        DomainAction.NewCombatBatchAction(new("combat-action"), new CombatBatch(new("fight-1"), "key-1", orders));

    [Fact]
    public void BatchBuildsOneCombatOrdersIntentThatRoundTripsTheWire()
    {
        IntentActionRules.RegisterIntentKinds();
        var action = Batch(
            new() { Kind = "draft", Pawn = new("p1") },
            new() { Kind = "move", Pawn = new("p1"), Cell = new(3, 4) },
            new() { Kind = "attack", Pawn = new("p1"), Target = new("raider") },
            new() { Kind = "fire_mode", Pawn = new("p2"), FireMode = "hold_fire" },
            new() { Kind = "door", Door = "hold_open", Cell = new(5, 6) },
            new() { Kind = "door", Pawn = new("p2"), Door = "open", Cell = new(5, 6) });

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var json = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);

        var wire = IntentActionRules.IntentAction("combat-action", decoded);
        var orders = wire.Intent!.CombatOrders!.Orders;
        Assert.Equal(6, orders.Count);
        Assert.IsType<Op.CombatOrderOrder.DraftCase>(orders[0].Order);
        Assert.Equal("p1", orders[0].Pawn!.EntityId);
        Assert.IsType<Op.CombatOrderOrder.MoveCase>(orders[1].Order);
        Assert.Equal("raider", ((Op.CombatOrderOrder.AttackCase)orders[2].Order!).Value.EntityId);
        Assert.IsType<Op.CombatOrderOrder.FireModeCase>(orders[3].Order);
        Assert.Null(orders[4].Pawn);
        Assert.Equal("p2", orders[5].Pawn!.EntityId);

        var restored = WireJson.Deserialize<Op.Action>(WireJson.Serialize(wire));
        Assert.Equal("combat-action", restored.Key);
        Assert.Equal(WireJson.Serialize(wire), WireJson.Serialize(restored));
    }
}
