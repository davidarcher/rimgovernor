using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The Actions/Apply arm of one combat batch: its commands, in order, as one CombatOrders intent. A draft command is the
// Draft order for its pawn; a door or mortar_fire command names no pawn. A command kind with no combat order (a
// psycast, a permit call) is no part of a batch and fails the build rather than being dropped.
public static partial class BridgeFunctions
{
    private static Op.Action CombatBatchAction(DomainAction action)
    {
        var batch = action.CombatBatch ?? throw Contract("not a combat batch action");
        var orders = new List<Op.CombatOrder>(batch.Orders.Count);
        foreach (var command in batch.Orders)
        {
            var named = command.Kind is not ("door" or "mortar_fire") || (command.Kind == "door" && command.Door is "open" or "shut");
            var order = new Op.CombatOrder
            {
                Pawn = named ? new Op.EntityPrecondition { EntityId = command.Pawn.Value } : null,
                Order = CombatOrderFor(command),
            };
            orders.Add(order);
        }
        var wire = new Op.CombatOrders { Orders = [.. orders] };
        ValidateCombatOrders(wire);
        return new Op.Action { Intent = wire };
    }

    private static Op.CombatOrderOrder CombatOrderFor(CombatCommand c) => c.Kind switch
    {
        "draft" => new Op.CombatOrderOrder.DraftCase(new Op.Clear()),
        "move" => new Op.CombatOrderOrder.MoveCase(WireCell(c.Cell)),
        "attack" => new Op.CombatOrderOrder.AttackCase(new Op.EntityPrecondition { EntityId = c.Target.Value }),
        "attack_ground" => new Op.CombatOrderOrder.AttackGroundCase(WireCell(c.Cell)),
        "fire_mode" => new Op.CombatOrderOrder.FireModeCase(FireModeOf(c.FireMode)),
        "stop" => new Op.CombatOrderOrder.StopCase(new Op.Clear()),
        "hold_position" => new Op.CombatOrderOrder.HoldPositionCase(new Op.Clear()),
        "door" => new Op.CombatOrderOrder.DoorCase(new Op.CombatDoor { Cell = WireCell(c.Cell), Mode = DoorModeOf(c.Door) }),
        "rescue" => new Op.CombatOrderOrder.RescueCase(new Op.CombatRescue { Downed = new Op.EntityPrecondition { EntityId = c.Target.Value } }),
        "repair" => new Op.CombatOrderOrder.RepairCase(new Op.CombatRepair { Cell = WireCell(c.Cell) }),
        "man_mortar" => new Op.CombatOrderOrder.ManMortarCase(WireCell(c.Cell)),
        "mortar_fire" => new Op.CombatOrderOrder.MortarFireCase(new Op.CombatMortarFire { Mortar = WireCell(c.Cell), Target = c.Clear ? null : WireCell(c.Aim), Shell = c.Clear || c.Shell.Length == 0 ? null : c.Shell }),
        "release" => new Op.CombatOrderOrder.ReleaseCase(new Op.EntityPrecondition { EntityId = c.Target.Value }),
        "animal_area" => new Op.CombatOrderOrder.AnimalAreaCase(new Op.CombatAnimalArea { Area = new Op.CombatAnimalAreaArea.CellCase(WireCell(c.Cell)) }),
        "animal_clear" => new Op.CombatOrderOrder.AnimalAreaCase(new Op.CombatAnimalArea { Area = new Op.CombatAnimalAreaArea.ClearCase(new Op.Clear()) }),
        "drug" => new Op.CombatOrderOrder.CombatDrugCase(c.Drug),
        _ => throw Contract($"combat command {c.Kind} has no combat order"),
    };

    private static Op.CombatFireMode FireModeOf(string name) => name switch
    {
        "fire_at_will" => Op.CombatFireMode.AtWill,
        "hold_fire" => Op.CombatFireMode.Hold,
        _ => throw Contract($"combat fire mode {name} unsupported"),
    };

    private static Op.CombatDoorMode DoorModeOf(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "holdopen" => Op.CombatDoorMode.HoldOpen,
        "close" => Op.CombatDoorMode.Close,
        "forbid" => Op.CombatDoorMode.Forbid,
        "allow" => Op.CombatDoorMode.Allow,
        "open" => Op.CombatDoorMode.Open,
        "shut" => Op.CombatDoorMode.Shut,
        _ => throw Contract($"combat door mode {name} unsupported"),
    };
}
