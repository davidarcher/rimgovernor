package bridge

import (
	"context"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// MaxCombatOrders bounds one combat.orders batch (#850), matching native.
const MaxCombatOrders = 64

// Combat order refusal reasons, one per CombatOrderResult.refusal.
// DraftOwnership is policy.DraftOwnership: the pawn is not an eligible
// drafted pawn under an existing native draft claim.
const (
	CombatRefusalDraftOwnership = "draft_ownership"
	CombatRefusalStaleSnapshot  = "stale_snapshot"
	CombatRefusalNotFound       = "not_found"
	CombatRefusalUnreachable    = "unreachable"
	CombatRefusalCannotHit      = "cannot_hit"
	CombatRefusalNoGroundVerb   = "no_ground_verb"
	CombatRefusalNotADoor       = "not_a_door"
	CombatRefusalNativeRefused  = "native_refused"
)

var combatRefusals = map[string]bool{
	CombatRefusalDraftOwnership: true, CombatRefusalStaleSnapshot: true, CombatRefusalNotFound: true,
	CombatRefusalUnreachable: true, CombatRefusalCannotHit: true, CombatRefusalNoGroundVerb: true,
	CombatRefusalNotADoor: true, CombatRefusalNativeRefused: true,
}

// CombatOrderResult is one order's outcome, in request order.
type CombatOrderResult struct {
	Index   int
	PawnID  string // empty for a door order
	Applied bool
	Refusal string // one of the CombatRefusal* reasons when not applied
	JobDef  string // the ordered job, when the order took one
}

// CombatOrdersControl issues batched combat micro orders under an owned draft.
type CombatOrdersControl struct{ client *Client }

func NewCombatOrdersControl(client *Client) (*CombatOrdersControl, error) {
	if client == nil {
		return nil, contract("combat orders client missing")
	}
	return &CombatOrdersControl{client: client}, nil
}

func combatOrdersOperation(command *o.CombatOrders) *o.Operation {
	return &o.Operation{Command: &o.Operation_CombatOrders{CombatOrders: command}}
}

// optionalTokenEntity is an exact entity whose snapshot token may be
// omitted (orders dispatched under a running clock, as #243).
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
	if len(command.Orders) < 1 || len(command.Orders) > MaxCombatOrders {
		return contract("combat orders outside 1..%d", MaxCombatOrders)
	}
	for i, order := range command.Orders {
		if order == nil {
			return contract("combat order %d missing", i)
		}
		if err := buildingUnknown(order); err != nil {
			return err
		}
		_, door := order.Order.(*o.CombatOrder_Door)
		if door {
			if order.Pawn != nil {
				return contract("combat order %d: a door order names no pawn", i)
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
			if m := v.Door.GetMode(); m != o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN && m != o.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE {
				return contract("combat order %d door mode unsupported", i)
			}
		case *o.CombatOrder_HoldPosition:
			if v.HoldPosition == nil {
				return contract("combat order %d hold_position missing", i)
			}
		case *o.CombatOrder_Stop:
			if v.Stop == nil {
				return contract("combat order %d stop missing", i)
			}
		default:
			return contract("combat order %d has no order", i)
		}
	}
	return nil
}

// CombatOrderResults decodes a combat.orders receipt against its request:
// one result per order, in order, each applied or refused with a known reason.
// An uncertain receipt carries no complete result set and returns nil results.
func CombatOrderResults(receipt *r.Receipt, command *o.CombatOrders) ([]CombatOrderResult, error) {
	if receipt == nil {
		return nil, contract("combat orders receipt missing")
	}
	var evidence *r.EffectEvidence
	applied := false
	switch v := receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		evidence, applied = v.Applied.GetObserved(), true
	case *r.Receipt_NoChange:
		if !diagnostic(v.NoChange.Detail) {
			return nil, contract("combat orders no-change detail missing")
		}
		evidence = v.NoChange.GetObserved()
	case *r.Receipt_Uncertain:
		return nil, nil
	default:
		return nil, contract("combat orders receipt outcome missing")
	}
	effect := evidence.GetCombatOrders()
	if effect == nil {
		return nil, contract("combat orders evidence missing")
	}
	if len(effect.Results) != len(command.GetOrders()) {
		return nil, contract("combat orders: %d results for %d orders", len(effect.Results), len(command.GetOrders()))
	}
	out := make([]CombatOrderResult, len(effect.Results))
	any := false
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
			any = true
		} else if !combatRefusals[res.Refusal] {
			return nil, contract("combat order result %d refused without a known reason: %q", i, res.Refusal)
		} else if v.JobDef != nil {
			return nil, contract("combat order result %d refused with a job", i)
		}
		out[i] = res
	}
	if any != applied {
		return nil, contract("combat orders receipt outcome disagrees with its results")
	}
	return out, nil
}

// Issue executes one combat.orders batch under the current authority
// generation and decodes its per-order results.
func (control *CombatOrdersControl) Issue(ctx context.Context, pre *a.WritePrecondition, command *o.CombatOrders) ([]CombatOrderResult, *o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, nil, Result{}, contract("combat orders capability missing")
	}
	if pre == nil || pre.GetExpectedGeneration() == 0 {
		return nil, nil, Result{}, contract("combat orders precondition missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, nil, Result{}, err
	}
	if err := ValidateIdentity(pre.Identity); err != nil {
		return nil, nil, Result{}, err
	}
	if err := buildingAttempt(pre.Attempt); err != nil {
		return nil, nil, Result{}, err
	}
	if err := ValidateCombatOrders(command); err != nil {
		return nil, nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	command = proto.Clone(command).(*o.CombatOrders)
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: combatOrdersOperation(command)}, reply)
	if err != nil {
		return nil, nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		if v.Receipt == nil || !proto.Equal(v.Receipt.Attempt, pre.Attempt) {
			return nil, reply, raw, contract("combat orders receipt attempt mismatch")
		}
		if err = buildingContext(v.Receipt.AdmittedContext, pre.Identity, pre.GetExpectedGeneration(), true); err != nil {
			return nil, reply, raw, err
		}
		results, err := CombatOrderResults(v.Receipt, command)
		for _, res := range results {
			logCombatOrder(ctx, res)
		}
		return results, reply, raw, err
	case *o.ExecuteReply_Failure:
		return nil, reply, raw, failure(v.Failure, raw)
	}
	return nil, reply, raw, contract("combat orders execute outcome missing")
}

// CombatOrders issues one combat.orders batch (CombatOrdersControl.Issue):
// the fight's orders at a stop (#852).
func (client *Client) CombatOrders(ctx context.Context, pre *a.WritePrecondition, command *o.CombatOrders) ([]CombatOrderResult, *o.ExecuteReply, Result, error) {
	return (&CombatOrdersControl{client: client}).Issue(ctx, pre, command)
}

// logCombatOrder writes one service-log line per order result, the
// combat_order ... outcome=<applied|refused> line combatlab metrics count.
func logCombatOrder(ctx context.Context, res CombatOrderResult) {
	attrs := []any{telemetry.ComponentKey, "combat-orders", telemetry.KindKey, "combat_order", "index", res.Index, "pawn", res.PawnID}
	if res.Applied {
		attrs = append(attrs, "outcome", "applied", "job", res.JobDef)
	} else {
		attrs = append(attrs, "outcome", "refused", "reason", res.Refusal)
	}
	slog.Default().InfoContext(ctx, "combat_order", attrs...)
}
