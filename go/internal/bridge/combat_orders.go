package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

// Combat order refusal reasons, one per CombatOrderResult.refusal.
const (
	// NotDrafted: a pawn order for a pawn that is not drafted.
	CombatRefusalNotDrafted    = "not_drafted"
	CombatRefusalStaleSnapshot = "stale_snapshot"
	CombatRefusalNotFound      = "not_found"
	CombatRefusalUnreachable   = "unreachable"
	CombatRefusalCannotHit     = "cannot_hit"
	CombatRefusalNoGroundVerb  = "no_ground_verb"
	CombatRefusalNotADoor      = "not_a_door"
	CombatRefusalNativeRefused = "native_refused"
	// Rescue refusals: the rescue eligibility or bed search failed.
	CombatRefusalCannotRescue = "cannot_rescue"
	CombatRefusalNoBed        = "no_bed"
	// Repair refusal: no damaged player building on the cell, or
	// the pawn cannot construct.
	CombatRefusalCannotRepair = "cannot_repair"
	// Draft refusal: the pawn is not eligible.
	CombatRefusalCannotDraft = "cannot_draft"
	// Mortar refusal: no unroofed player mortar on the cell.
	CombatRefusalNotAMortar = "not_a_mortar"
	// Mortar shell refusals: the mortar does not accept the shell,
	// or no unforbidden stack of it is in reach.
	CombatRefusalUnknownShell = "unknown_shell"
	CombatRefusalNoShell      = "no_shell"
	// Animal order refusals: not a spawned player animal, or a
	// release order to an animal without the Release training.
	CombatRefusalNotOurs   = "not_ours"
	CombatRefusalUntrained = "untrained"
	// Combat drug refusals: eligibility, inventory and chemical safety checks.
	CombatRefusalChild       = "child"
	CombatRefusalNotADrug    = "not_a_drug"
	CombatRefusalNoDrug      = "no_drug"
	CombatRefusalAlreadyHigh = "already_high"
	CombatRefusalDrugRisk    = "drug_risk"
)

var combatRefusals = map[string]bool{
	CombatRefusalNotDrafted: true, CombatRefusalStaleSnapshot: true, CombatRefusalNotFound: true,
	CombatRefusalUnreachable: true, CombatRefusalCannotHit: true, CombatRefusalNoGroundVerb: true,
	CombatRefusalNotADoor: true, CombatRefusalNativeRefused: true,
	CombatRefusalCannotRescue: true, CombatRefusalNoBed: true, CombatRefusalCannotRepair: true,
	CombatRefusalCannotDraft: true, CombatRefusalNotAMortar: true,
	CombatRefusalUnknownShell: true, CombatRefusalNoShell: true,
	CombatRefusalNotOurs: true, CombatRefusalUntrained: true,
	CombatRefusalChild: true, CombatRefusalNotADrug: true, CombatRefusalNoDrug: true,
	CombatRefusalAlreadyHigh: true, CombatRefusalDrugRisk: true,
}

// CombatOrderResult is one order's outcome, in request order.
type CombatOrderResult struct {
	Index   int
	PawnID  string // empty for a door order
	Applied bool
	Refusal string // one of the CombatRefusal* reasons when not applied
	JobDef  string // the ordered job, when the order took one
}

// optionalTokenEntity is an exact entity whose snapshot token may be
// omitted (orders dispatched under a running clock).
func optionalTokenEntity(v *o.EntityPrecondition) error {
	if v == nil {
		return contract("combat order entity missing")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := validID(v.GetEntityId()); err != nil {
		return err
	}
	if v.ExpectedSnapshotToken != nil {
		return validID(v.GetExpectedSnapshotToken())
	}
	return nil
}

// ValidateCombatOrders applies the native request rules before any call.
func ValidateCombatOrders(command *o.CombatOrders) error {
	if command == nil {
		return contract("combat orders missing")
	}
	if err := buildingUnknown(command); err != nil {
		return err
	}
	if len(command.Orders) < 1 {
		return contract("combat orders need at least one order")
	}
	for i, order := range command.Orders {
		if order == nil {
			return contract("combat order %d missing", i)
		}
		if err := buildingUnknown(order); err != nil {
			return err
		}
		_, door := order.Order.(*o.CombatOrder_Door)
		_, fire := order.Order.(*o.CombatOrder_MortarFire)
		if door || fire {
			if order.Pawn != nil {
				return contract("combat order %d: a door or mortar_fire order names no pawn", i)
			}
		} else if err := optionalTokenEntity(order.Pawn); err != nil {
			return contract("combat order %d pawn: %v", i, err)
		}
		switch v := order.Order.(type) {
		case *o.CombatOrder_Move:
			if err := movementCell(v.Move); err != nil {
				return contract("combat order %d move: %v", i, err)
			}
		case *o.CombatOrder_AttackGround:
			if err := movementCell(v.AttackGround); err != nil {
				return contract("combat order %d attack_ground: %v", i, err)
			}
		case *o.CombatOrder_Attack:
			if err := optionalTokenEntity(v.Attack); err != nil {
				return contract("combat order %d target: %v", i, err)
			}
			if v.Attack.GetEntityId() == order.Pawn.GetEntityId() {
				return contract("combat order %d attacks its own pawn", i)
			}
		case *o.CombatOrder_FireMode:
			if v.FireMode != o.CombatFireMode_COMBAT_FIRE_MODE_AT_WILL && v.FireMode != o.CombatFireMode_COMBAT_FIRE_MODE_HOLD {
				return contract("combat order %d fire mode unsupported", i)
			}
		case *o.CombatOrder_Door:
			if v.Door == nil || movementCell(v.Door.Cell) != nil {
				return contract("combat order %d door cell missing or invalid", i)
			}
			if m := v.Door.GetMode(); m < o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN || m > o.CombatDoorMode_COMBAT_DOOR_MODE_ALLOW {
				return contract("combat order %d door mode unsupported", i)
			}
		case *o.CombatOrder_Rescue:
			if v.Rescue == nil {
				return contract("combat order %d rescue missing", i)
			}
			if err := optionalTokenEntity(v.Rescue.Downed); err != nil {
				return contract("combat order %d downed: %v", i, err)
			}
			if v.Rescue.Downed.GetEntityId() == order.Pawn.GetEntityId() {
				return contract("combat order %d rescues its own pawn", i)
			}
			if v.Rescue.Dest != nil && movementCell(v.Rescue.Dest) != nil {
				return contract("combat order %d rescue dest invalid", i)
			}
		case *o.CombatOrder_Repair:
			if v.Repair == nil || movementCell(v.Repair.Cell) != nil {
				return contract("combat order %d repair cell missing or invalid", i)
			}
		case *o.CombatOrder_ManMortar:
			if err := movementCell(v.ManMortar); err != nil {
				return contract("combat order %d man_mortar: %v", i, err)
			}
		case *o.CombatOrder_MortarFire:
			// No target clears the forced target, and names no shell.
			if v.MortarFire == nil || movementCell(v.MortarFire.Mortar) != nil ||
				(v.MortarFire.Target != nil && movementCell(v.MortarFire.Target) != nil) ||
				(v.MortarFire.Target == nil && v.MortarFire.Shell != nil) {
				return contract("combat order %d mortar_fire mortar or target cell missing or invalid", i)
			}
		case *o.CombatOrder_Release:
			if err := optionalTokenEntity(v.Release); err != nil {
				return contract("combat order %d release target: %v", i, err)
			}
			if v.Release.GetEntityId() == order.Pawn.GetEntityId() {
				return contract("combat order %d releases its animal on itself", i)
			}
		case *o.CombatOrder_AnimalArea:
			switch a := v.AnimalArea.GetArea().(type) {
			case *o.CombatAnimalArea_Cell:
				if err := movementCell(a.Cell); err != nil {
					return contract("combat order %d animal_area: %v", i, err)
				}
			case *o.CombatAnimalArea_Clear:
				if a.Clear == nil {
					return contract("combat order %d animal_area clear missing", i)
				}
			default:
				return contract("combat order %d animal_area needs a cell or clear", i)
			}
		case *o.CombatOrder_HoldPosition:
			if v.HoldPosition == nil {
				return contract("combat order %d hold_position missing", i)
			}
		case *o.CombatOrder_Stop:
			if v.Stop == nil {
				return contract("combat order %d stop missing", i)
			}
		case *o.CombatOrder_Draft:
			if v.Draft == nil {
				return contract("combat order %d draft missing", i)
			}
		case *o.CombatOrder_CombatDrug:
			if validID(v.CombatDrug) != nil {
				return contract("combat order %d combat_drug is not a def name", i)
			}
		default:
			return contract("combat order %d has no order", i)
		}
	}
	return nil
}

// CombatOrderResults decodes an applied combat_orders action receipt
// against its request: one result per order, in order, each applied or
// refused with a known reason. An uncertain receipt carries no result set
// and returns nil results.
func CombatOrderResults(receipt *r.Receipt, command *o.CombatOrders) ([]CombatOrderResult, error) {
	if _, uncertain := receipt.GetOutcome().(*r.Receipt_Uncertain); uncertain {
		return nil, nil
	}
	applied, ok := receipt.GetOutcome().(*r.Receipt_Applied)
	if !ok {
		return nil, contract("combat orders receipt not applied")
	}
	effect := applied.Applied.GetObserved().GetCombatOrders()
	if effect == nil {
		return nil, contract("combat orders evidence missing")
	}
	if len(effect.Results) != len(command.GetOrders()) {
		return nil, contract("combat orders: %d results for %d orders", len(effect.Results), len(command.GetOrders()))
	}
	out := make([]CombatOrderResult, len(effect.Results))
	for i, v := range effect.Results {
		if v == nil || v.Index == nil || int(v.GetIndex()) != i || v.Applied == nil {
			return nil, contract("combat order result %d index or outcome missing", i)
		}
		if v.GetPawnId() != command.Orders[i].GetPawn().GetEntityId() {
			return nil, contract("combat order result %d names pawn %q, want %q", i, v.GetPawnId(), command.Orders[i].GetPawn().GetEntityId())
		}
		res := CombatOrderResult{Index: i, PawnID: v.GetPawnId(), Applied: v.GetApplied(), Refusal: v.GetRefusal(), JobDef: v.GetJobDef()}
		if res.Applied {
			if v.Refusal != nil {
				return nil, contract("combat order result %d applied with a refusal", i)
			}
			if res.JobDef != "" && validID(res.JobDef) != nil {
				return nil, contract("combat order result %d job invalid", i)
			}
		} else if !combatRefusals[res.Refusal] {
			return nil, contract("combat order result %d refused without a known reason: %q", i, res.Refusal)
		} else if v.JobDef != nil {
			return nil, contract("combat order result %d refused with a job", i)
		}
		out[i] = res
	}
	return out, nil
}

// RecordCombatOrder writes one combat_order decision row per order result:
// applied (reason empty, attrs job) or refused (reason the refusal).
func RecordCombatOrder(ctx context.Context, res CombatOrderResult) {
	d := telemetry.Decision{Kind: "combat_order", Component: "combat-orders", Target: res.PawnID, Attrs: map[string]any{"index": res.Index}}
	if res.Applied {
		d.Verdict, d.Attrs["job"] = "applied", res.JobDef
	} else {
		d.Verdict, d.Reason = "refused", res.Refusal
	}
	telemetry.Decide(ctx, d)
}
