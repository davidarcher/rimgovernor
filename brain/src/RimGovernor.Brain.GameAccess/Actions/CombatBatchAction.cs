
namespace RimGovernor.Brain.GameAccess;

// The Actions/Apply arm of one combat batch: its commands, in order, as one CombatOrders intent. A draft command is the
// Draft order for its pawn; a door or mortar_fire command names no pawn. A command kind with no combat order (a
// psycast, a permit call) is no part of a batch and fails the build rather than being dropped.
public static class CombatBatchActionRules
{
    internal static RimGovernor.Brain.GameOrders.Operations.Action CombatBatchAction(DomainAction action)
    {
        var batch = action.CombatBatch ?? throw BridgeContractException.Contract("not a combat batch action");
        var orders = new List<RimGovernor.Brain.GameOrders.Operations.CombatOrder>(batch.Orders.Count);
        foreach (var command in batch.Orders)
        {
            var named = command.Kind is not ("door" or "mortar_fire") || (command.Kind == "door" && command.Door is "open" or "shut");
            var order = new RimGovernor.Brain.GameOrders.Operations.CombatOrder
            {
                Pawn = named ? new RimGovernor.Brain.GameOrders.Operations.EntityPrecondition { EntityId = command.Pawn.Value } : null,
                Order = CombatOrderFor(command),
            };
            orders.Add(order);
        }
        var wire = new RimGovernor.Brain.GameOrders.Operations.CombatOrders { Orders = [.. orders] };
        CombatOrdersRules.ValidateCombatOrders(wire);
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = wire };
    }

    private static RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder CombatOrderFor(CombatCommand c) => c.Kind switch
    {
        "draft" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DraftCase(new RimGovernor.Brain.GameOrders.Operations.Clear()),
        "move" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MoveCase(IntentActionRules.WireCell(c.Cell)),
        "attack" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackCase(new RimGovernor.Brain.GameOrders.Operations.EntityPrecondition { EntityId = c.Target.Value }),
        "attack_ground" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackGroundCase(IntentActionRules.WireCell(c.Cell)),
        "fire_mode" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase(FireModeOf(c.FireMode)),
        "stop" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.StopCase(new RimGovernor.Brain.GameOrders.Operations.Clear()),
        "hold_position" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.HoldPositionCase(new RimGovernor.Brain.GameOrders.Operations.Clear()),
        "door" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = IntentActionRules.WireCell(c.Cell), Mode = DoorModeOf(c.Door) }),
        "rescue" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RescueCase(new RimGovernor.Brain.GameOrders.Operations.CombatRescue { Bed = new RimGovernor.Brain.GameOrders.Operations.RescueBed.Search(), Downed = new RimGovernor.Brain.GameOrders.Operations.EntityPrecondition { EntityId = c.Target.Value } }),
        "repair" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RepairCase(new RimGovernor.Brain.GameOrders.Operations.CombatRepair { Cell = IntentActionRules.WireCell(c.Cell) }),
        "man_mortar" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ManMortarCase(IntentActionRules.WireCell(c.Cell)),
        "mortar_fire" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MortarFireCase(new RimGovernor.Brain.GameOrders.Operations.CombatMortarFire { Mortar = IntentActionRules.WireCell(c.Cell), Aim = c.Clear ? new RimGovernor.Brain.GameOrders.Operations.MortarAim.Cleared() : new RimGovernor.Brain.GameOrders.Operations.MortarAim.At(c.Aim), Shell = c.Clear || c.Shell.Length == 0 ? null : c.Shell }),
        "release" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ReleaseCase(new RimGovernor.Brain.GameOrders.Operations.EntityPrecondition { EntityId = c.Target.Value }),
        "animal_area" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalArea { Area = new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase(IntentActionRules.WireCell(c.Cell)) }),
        "animal_clear" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase(new RimGovernor.Brain.GameOrders.Operations.CombatAnimalArea { Area = new RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.ClearCase(new RimGovernor.Brain.GameOrders.Operations.Clear()) }),
        "drug" => new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.CombatDrugCase(c.Drug),
        _ => throw BridgeContractException.Contract($"combat command {c.Kind} has no combat order"),
    };

    private static RimGovernor.Brain.GameFacts.Operations.CombatFireMode FireModeOf(string name) => name switch
    {
        "fire_at_will" => RimGovernor.Brain.GameFacts.Operations.CombatFireMode.AtWill,
        "hold_fire" => RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold,
        _ => throw BridgeContractException.Contract($"combat fire mode {name} unsupported"),
    };

    private static RimGovernor.Brain.GameOrders.Operations.CombatDoorMode DoorModeOf(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "holdopen" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.HoldOpen,
        "close" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Close,
        "forbid" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Forbid,
        "allow" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Allow,
        "open" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Open,
        "shut" => RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Shut,
        _ => throw BridgeContractException.Contract($"combat door mode {name} unsupported"),
    };
}
