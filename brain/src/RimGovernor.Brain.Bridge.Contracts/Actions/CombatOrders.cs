using Op = RimGovernor.Protocol.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go combat_orders.go: ValidateCombatOrders and its optionalTokenEntity. The receipt decoding
// (CombatOrderResults) and the combat call belong to the combat call port, not to the builders.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>optionalTokenEntity</c>: an exact entity whose snapshot token may be omitted (orders dispatched under a running clock).</summary>
    private static void OptionalTokenEntity(Op.EntityPrecondition? value)
    {
        if (value is null)
        {
            throw Contract("combat order entity missing");
        }
        BuildingUnknown(value);
        ValidId(value.EntityId);
        if (value.HasExpectedSnapshotToken)
        {
            ValidId(value.ExpectedSnapshotToken);
        }
    }

    /// <summary>Go's <c>ValidateCombatOrders</c>: applies the native request rules before any call.</summary>
    /// <exception cref="BridgeContractException">The batch is malformed.</exception>
    public static void ValidateCombatOrders(Op.CombatOrders? command)
    {
        if (command is null)
        {
            throw Contract("combat orders missing");
        }
        BuildingUnknown(command);
        if (command.Orders.Count < 1)
        {
            throw Contract("combat orders need at least one order");
        }
        for (var i = 0; i < command.Orders.Count; i++)
        {
            var order = command.Orders[i] ?? throw Contract($"combat order {i} missing");
            BuildingUnknown(order);
            var door = order.OrderCase == Op.CombatOrder.OrderOneofCase.Door;
            var fire = order.OrderCase == Op.CombatOrder.OrderOneofCase.MortarFire;
            if (door || fire)
            {
                if (order.Pawn is not null)
                {
                    throw Contract($"combat order {i}: a door or mortar_fire order names no pawn");
                }
            }
            else
            {
                Check(() => OptionalTokenEntity(order.Pawn), $"combat order {i} pawn");
            }
            var pawn = order.Pawn?.EntityId ?? "";
            switch (order.OrderCase)
            {
                case Op.CombatOrder.OrderOneofCase.Move:
                    Check(() => MovementCell(order.Move), $"combat order {i} move");
                    break;
                case Op.CombatOrder.OrderOneofCase.AttackGround:
                    Check(() => MovementCell(order.AttackGround), $"combat order {i} attack_ground");
                    break;
                case Op.CombatOrder.OrderOneofCase.Attack:
                    Check(() => OptionalTokenEntity(order.Attack), $"combat order {i} target");
                    if (order.Attack.EntityId == pawn)
                    {
                        throw Contract($"combat order {i} attacks its own pawn");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.FireMode:
                    if (order.FireMode is not (Op.CombatFireMode.AtWill or Op.CombatFireMode.Hold))
                    {
                        throw Contract($"combat order {i} fire mode unsupported");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.Door:
                    if (!Valid(() => MovementCell(order.Door.Cell)))
                    {
                        throw Contract($"combat order {i} door cell missing or invalid");
                    }
                    if (order.Door.Mode is < Op.CombatDoorMode.HoldOpen or > Op.CombatDoorMode.Allow)
                    {
                        throw Contract($"combat order {i} door mode unsupported");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.Rescue:
                    Check(() => OptionalTokenEntity(order.Rescue.Downed), $"combat order {i} downed");
                    if (order.Rescue.Downed.EntityId == pawn)
                    {
                        throw Contract($"combat order {i} rescues its own pawn");
                    }
                    if (order.Rescue.Dest is not null && !Valid(() => MovementCell(order.Rescue.Dest)))
                    {
                        throw Contract($"combat order {i} rescue dest invalid");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.Repair:
                    if (!Valid(() => MovementCell(order.Repair.Cell)))
                    {
                        throw Contract($"combat order {i} repair cell missing or invalid");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.ManMortar:
                    Check(() => MovementCell(order.ManMortar), $"combat order {i} man_mortar");
                    break;
                case Op.CombatOrder.OrderOneofCase.MortarFire:
                    // No target clears the forced target, and names no shell.
                    var mortar = order.MortarFire;
                    if (!Valid(() => MovementCell(mortar.Mortar)) || (mortar.Target is not null && !Valid(() => MovementCell(mortar.Target))) || (mortar.Target is null && mortar.HasShell))
                    {
                        throw Contract($"combat order {i} mortar_fire mortar or target cell missing or invalid");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.Release:
                    Check(() => OptionalTokenEntity(order.Release), $"combat order {i} release target");
                    if (order.Release.EntityId == pawn)
                    {
                        throw Contract($"combat order {i} releases its animal on itself");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.AnimalArea:
                    switch (order.AnimalArea.AreaCase)
                    {
                        case Op.CombatAnimalArea.AreaOneofCase.Cell:
                            Check(() => MovementCell(order.AnimalArea.Cell), $"combat order {i} animal_area");
                            break;
                        case Op.CombatAnimalArea.AreaOneofCase.Clear:
                            break;
                        default:
                            throw Contract($"combat order {i} animal_area needs a cell or clear");
                    }
                    break;
                case Op.CombatOrder.OrderOneofCase.HoldPosition:
                case Op.CombatOrder.OrderOneofCase.Stop:
                case Op.CombatOrder.OrderOneofCase.Draft:
                    break;
                case Op.CombatOrder.OrderOneofCase.CombatDrug:
                    if (!IsValidId(order.CombatDrug))
                    {
                        throw Contract($"combat order {i} combat_drug is not a def name");
                    }
                    break;
                default:
                    throw Contract($"combat order {i} has no order");
            }
        }
    }

    private static bool Valid(Action check)
    {
        try
        {
            check();
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }

    // Go's contract("combat order %d move: %v", i, err): the failing check's message after the order's label.
    private static void Check(Action check, string label)
    {
        try
        {
            check();
        }
        catch (BridgeContractException e)
        {
            throw Contract($"{label}: {e.Message}");
        }
    }
}
