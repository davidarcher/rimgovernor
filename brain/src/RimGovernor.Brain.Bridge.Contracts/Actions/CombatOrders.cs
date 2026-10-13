using Op = RimGovernor.Contracts.Wire.Operations;

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
        ValidId((value.EntityId ?? ""));
        if (value.ExpectedSnapshotToken is not null)
        {
            ValidId((value.ExpectedSnapshotToken ?? ""));
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
            var door = order.Order is Op.CombatOrderOrder.DoorCase;
            var fire = order.Order is Op.CombatOrderOrder.MortarFireCase;
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
            switch (order.Order)
            {
                case Op.CombatOrderOrder.MoveCase { Value: var move }:
                    Check(() => MovementCell(move), $"combat order {i} move");
                    break;
                case Op.CombatOrderOrder.AttackGroundCase { Value: var ground }:
                    Check(() => MovementCell(ground), $"combat order {i} attack_ground");
                    break;
                case Op.CombatOrderOrder.AttackCase { Value: var attack }:
                    Check(() => OptionalTokenEntity(attack), $"combat order {i} target");
                    if ((attack.EntityId ?? "") == pawn)
                    {
                        throw Contract($"combat order {i} attacks its own pawn");
                    }
                    break;
                case Op.CombatOrderOrder.FireModeCase { Value: var fireMode }:
                    if (fireMode is not (Op.CombatFireMode.AtWill or Op.CombatFireMode.Hold))
                    {
                        throw Contract($"combat order {i} fire mode unsupported");
                    }
                    break;
                case Op.CombatOrderOrder.DoorCase { Value: var doorOrder }:
                    if (!Valid(() => MovementCell(doorOrder.Cell)))
                    {
                        throw Contract($"combat order {i} door cell missing or invalid");
                    }
                    if ((doorOrder.Mode ?? Op.CombatDoorMode.Unspecified) is < Op.CombatDoorMode.HoldOpen or > Op.CombatDoorMode.Allow)
                    {
                        throw Contract($"combat order {i} door mode unsupported");
                    }
                    break;
                case Op.CombatOrderOrder.RescueCase { Value: var rescue }:
                    Check(() => OptionalTokenEntity(rescue.Downed), $"combat order {i} downed");
                    if ((rescue.Downed?.EntityId ?? "") == pawn)
                    {
                        throw Contract($"combat order {i} rescues its own pawn");
                    }
                    if (rescue.Dest is not null && !Valid(() => MovementCell(rescue.Dest)))
                    {
                        throw Contract($"combat order {i} rescue dest invalid");
                    }
                    break;
                case Op.CombatOrderOrder.RepairCase { Value: var repair }:
                    if (!Valid(() => MovementCell(repair.Cell)))
                    {
                        throw Contract($"combat order {i} repair cell missing or invalid");
                    }
                    break;
                case Op.CombatOrderOrder.ManMortarCase { Value: var manMortar }:
                    Check(() => MovementCell(manMortar), $"combat order {i} man_mortar");
                    break;
                case Op.CombatOrderOrder.MortarFireCase { Value: var mortar }:
                    // No target clears the forced target, and names no shell.
                    if (!Valid(() => MovementCell(mortar.Mortar)) || (mortar.Target is not null && !Valid(() => MovementCell(mortar.Target))) || (mortar.Target is null && mortar.Shell is not null))
                    {
                        throw Contract($"combat order {i} mortar_fire mortar or target cell missing or invalid");
                    }
                    break;
                case Op.CombatOrderOrder.ReleaseCase { Value: var release }:
                    Check(() => OptionalTokenEntity(release), $"combat order {i} release target");
                    if ((release.EntityId ?? "") == pawn)
                    {
                        throw Contract($"combat order {i} releases its animal on itself");
                    }
                    break;
                case Op.CombatOrderOrder.AnimalAreaCase { Value: var animalArea }:
                    switch (animalArea.Area)
                    {
                        case Op.CombatAnimalAreaArea.CellCase { Value: var areaCell }:
                            Check(() => MovementCell(areaCell), $"combat order {i} animal_area");
                            break;
                        case Op.CombatAnimalAreaArea.ClearCase:
                            break;
                        default:
                            throw Contract($"combat order {i} animal_area needs a cell or clear");
                    }
                    break;
                case Op.CombatOrderOrder.HoldPositionCase:
                case Op.CombatOrderOrder.StopCase:
                case Op.CombatOrderOrder.DraftCase:
                    break;
                case Op.CombatOrderOrder.CombatDrugCase { Value: var drug }:
                    if (!IsValidId(drug ?? ""))
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
