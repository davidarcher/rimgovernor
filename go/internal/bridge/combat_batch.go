package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func init() { intentKinds[domain.CombatBatchAction] = combatBatchAction }
func combatBatchAction(action domain.Action) (*op.Action, error) {
	batch, ok := action.CombatBatch()
	if !ok {
		return nil, contract("combat batch missing")
	}
	command := &op.CombatOrders{}
	for _, order := range batch.Orders() {
		wire := &op.CombatOrder{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(order.Pawn))}}
		switch order.Kind {
		case "draft":
			wire.Order = &op.CombatOrder_Draft{Draft: &op.Clear{}}
		case "move":
			wire.Order = &op.CombatOrder_Move{Move: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
		case "attack":
			wire.Order = &op.CombatOrder_Attack{Attack: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}
		case "rescue":
			wire.Order = &op.CombatOrder_Rescue{Rescue: &op.CombatRescue{Downed: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}}
		case "man_mortar":
			wire.Order = &op.CombatOrder_ManMortar{ManMortar: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
		case "mortar_fire":
			wire.Pawn = nil
			fire := &op.CombatMortarFire{Mortar: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
			if !order.Clear {
				// No target clears the forced target.
				fire.Target = &c.Cell{X: proto.Int32(order.Aim.X), Z: proto.Int32(order.Aim.Z)}
			}
			if order.Shell != "" {
				fire.Shell = proto.String(order.Shell)
			}
			wire.Order = &op.CombatOrder_MortarFire{MortarFire: fire}
		case "attack_ground":
			wire.Order = &op.CombatOrder_AttackGround{AttackGround: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
		case "repair":
			wire.Order = &op.CombatOrder_Repair{Repair: &op.CombatRepair{Cell: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}}
		case "door":
			mode := op.CombatDoorMode_COMBAT_DOOR_MODE_FORBID
			switch order.Door {
			case "allow":
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_ALLOW
			case "hold_open":
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN
			case "close":
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE
			}
			wire.Pawn = nil
			wire.Order = &op.CombatOrder_Door{Door: &op.CombatDoor{Cell: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}, Mode: mode.Enum()}}
		case "stop":
			wire.Order = &op.CombatOrder_Stop{Stop: &op.Clear{}}
		case "hold_position":
			wire.Order = &op.CombatOrder_HoldPosition{HoldPosition: &op.Clear{}}
		case "release":
			wire.Order = &op.CombatOrder_Release{Release: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}
		case "animal_area":
			wire.Order = &op.CombatOrder_AnimalArea{AnimalArea: &op.CombatAnimalArea{Area: &op.CombatAnimalArea_Cell{Cell: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}}}
		case "animal_clear":
			wire.Order = &op.CombatOrder_AnimalArea{AnimalArea: &op.CombatAnimalArea{Area: &op.CombatAnimalArea_Clear{Clear: &op.Clear{}}}}
		case "fire_mode":
			mode := op.CombatFireMode_COMBAT_FIRE_MODE_AT_WILL
			if order.FireMode == "hold_fire" {
				mode = op.CombatFireMode_COMBAT_FIRE_MODE_HOLD
			}
			wire.Order = &op.CombatOrder_FireMode{FireMode: mode}
		case "drug":
			wire.Order = &op.CombatOrder_CombatDrug{CombatDrug: order.Drug}
		default:
			return nil, contract("unsupported combat order")
		}
		command.Orders = append(command.Orders, wire)
	}

	if err := ValidateCombatOrders(command); err != nil {
		return nil, err
	}
	return &op.Action{Intent: &op.Action_CombatOrders{CombatOrders: command}}, nil
}
