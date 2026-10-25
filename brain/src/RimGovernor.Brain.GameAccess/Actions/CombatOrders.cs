
namespace RimGovernor.Brain.GameAccess;

// ValidateCombatOrders and its optionalTokenEntity. The receipt decoding
// (CombatOrderResults) and the combat call belong to the combat call port, not to the builders.
public static class CombatOrdersRules
{
    /// <summary>An exact entity whose snapshot token may be omitted (orders dispatched under a running clock).</summary>
    private static void OptionalTokenEntity(RimGovernor.Brain.GameOrders.Operations.EntityPrecondition? value)
    {
        if (value is null)
        {
            throw BridgeContractException.Contract("combat order entity missing");
        }
        WireValidation.ValidId((value.EntityId ?? ""));
        if (value.ExpectedSnapshotToken is not null)
        {
            WireValidation.ValidId((value.ExpectedSnapshotToken ?? ""));
        }
    }

    /// <summary>Applies the native request rules before any call.</summary>
    /// <exception cref="BridgeContractException">The batch is malformed.</exception>
    public static void ValidateCombatOrders(RimGovernor.Brain.GameOrders.Operations.CombatOrders? command)
    {
        if (command is null)
        {
            throw BridgeContractException.Contract("combat orders missing");
        }
        if (command.Orders.Count < 1)
        {
            throw BridgeContractException.Contract("combat orders need at least one order");
        }
        for (var i = 0; i < command.Orders.Count; i++)
        {
            var order = command.Orders[i] ?? throw BridgeContractException.Contract($"combat order {i} missing");
            var door = order.Order is RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase { Value.Mode: not (RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Open or RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Shut) };
            var fire = order.Order is RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MortarFireCase;
            if (door || fire)
            {
                if (order.Pawn is not null)
                {
                    throw BridgeContractException.Contract($"combat order {i}: a door or mortar_fire order names no pawn");
                }
            }
            else
            {
                Check(() => OptionalTokenEntity(order.Pawn), $"combat order {i} pawn");
            }
            var pawn = order.Pawn?.EntityId ?? "";
            ValidateCombatOrder(i, pawn, order.Order);
        }
    }

    /// <summary>The order-specific rules of combat order <paramref name="i"/>, whose pawn is <paramref name="pawn"/> (empty for none).</summary>
    private static void ValidateCombatOrder(int i, string pawn, RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder? order)
    {
        switch (order)
        {
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MoveCase { Value: var move }:
                Check(() => BuildingsRules.MovementCell(move), $"combat order {i} move");
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackGroundCase { Value: var ground }:
                Check(() => BuildingsRules.MovementCell(ground), $"combat order {i} attack_ground");
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AttackCase { Value: var attack }:
                Check(() => OptionalTokenEntity(attack), $"combat order {i} target");
                if ((attack.EntityId ?? "") == pawn)
                {
                    throw BridgeContractException.Contract($"combat order {i} attacks its own pawn");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.FireModeCase { Value: var fireMode }:
                if (fireMode is not (RimGovernor.Brain.GameFacts.Operations.CombatFireMode.AtWill or RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold))
                {
                    throw BridgeContractException.Contract($"combat order {i} fire mode unsupported");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase { Value: var doorOrder }:
                if (!Valid(() => BuildingsRules.MovementCell(doorOrder.Cell)))
                {
                    throw BridgeContractException.Contract($"combat order {i} door cell missing or invalid");
                }
                if (doorOrder.Mode is < RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.HoldOpen or > RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Shut)
                {
                    throw BridgeContractException.Contract($"combat order {i} door mode unsupported");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RescueCase { Value: var rescue }:
                Check(() => OptionalTokenEntity(rescue.Downed), $"combat order {i} downed");
                if ((rescue.Downed?.EntityId ?? "") == pawn)
                {
                    throw BridgeContractException.Contract($"combat order {i} rescues its own pawn");
                }
                if (rescue.Bed is RimGovernor.Brain.GameOrders.Operations.RescueBed.At { Cell: var bed } && !Valid(() => BuildingsRules.MovementCell(bed)) || rescue.Bed is not (RimGovernor.Brain.GameOrders.Operations.RescueBed.At or RimGovernor.Brain.GameOrders.Operations.RescueBed.Search))
                {
                    throw BridgeContractException.Contract($"combat order {i} rescue dest invalid");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.RepairCase { Value: var repair }:
                if (!Valid(() => BuildingsRules.MovementCell(repair.Cell)))
                {
                    throw BridgeContractException.Contract($"combat order {i} repair cell missing or invalid");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ManMortarCase { Value: var manMortar }:
                Check(() => BuildingsRules.MovementCell(manMortar), $"combat order {i} man_mortar");
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.MortarFireCase { Value: var mortar }:
                // No target clears the forced target, and names no shell.
                if (!Valid(() => BuildingsRules.MovementCell(mortar.Mortar)) || (mortar.Aim is RimGovernor.Brain.GameOrders.Operations.MortarAim.At { Cell: var aim } && !Valid(() => BuildingsRules.MovementCell(aim))) || (mortar.Aim is RimGovernor.Brain.GameOrders.Operations.MortarAim.Cleared && mortar.Shell is not null) || mortar.Aim is not (RimGovernor.Brain.GameOrders.Operations.MortarAim.Cleared or RimGovernor.Brain.GameOrders.Operations.MortarAim.At))
                {
                    throw BridgeContractException.Contract($"combat order {i} mortar_fire mortar or target cell missing or invalid");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.ReleaseCase { Value: var release }:
                Check(() => OptionalTokenEntity(release), $"combat order {i} release target");
                if ((release.EntityId ?? "") == pawn)
                {
                    throw BridgeContractException.Contract($"combat order {i} releases its animal on itself");
                }
                return;
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.AnimalAreaCase { Value: var animalArea }:
                switch (animalArea.Area)
                {
                    case RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.CellCase { Value: var areaCell }:
                        Check(() => BuildingsRules.MovementCell(areaCell), $"combat order {i} animal_area");
                        return;
                    case RimGovernor.Brain.GameOrders.Operations.CombatAnimalAreaArea.ClearCase:
                        return;
                }
                throw BridgeContractException.Contract($"combat order {i} animal_area needs a cell or clear");
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.HoldPositionCase:
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.StopCase:
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DraftCase:
            case RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.CombatDrugCase:
                return;
        }
        throw BridgeContractException.Contract($"combat order {i} has no order");
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

    // The failing check's message after the order's label.
    private static void Check(Action check, string label)
    {
        try
        {
            check();
        }
        catch (BridgeContractException e)
        {
            throw BridgeContractException.Contract($"{label}: {e.Message}");
        }
    }
}
