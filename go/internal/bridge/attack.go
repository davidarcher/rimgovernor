package bridge

import (
	"context"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func attackOperation(command *o.AttackTarget) *o.Operation {
	return &o.Operation{Command: &o.Operation_AttackTarget{AttackTarget: command}}
}

// attackJobDef is the only native job def a given explicit attack mode may
// report. Auto is not accepted here: this contract requires the caller to
// have already chosen melee or ranged, matching squad composition's explicit
// per-defender mode assignment and keeping explosive-verb selection outside
// Go's control (native's own ranged attribution allowlist governs that).
func attackJobDef(mode o.AttackMode) string {
	switch mode {
	case o.AttackMode_ATTACK_MODE_MELEE:
		return "AttackMelee"
	case o.AttackMode_ATTACK_MODE_RANGED:
		return "AttackStatic"
	default:
		return ""
	}
}
func attackCommand(command *o.AttackTarget) error {
	if command == nil {
		return contract("attack command missing")
	}
	if err := buildingUnknown(command); err != nil {
		return err
	}
	if err := draftEntity(command.Pawn); err != nil {
		return err
	}
	if err := draftEntity(command.Target); err != nil {
		return err
	}
	if command.Pawn.GetEntityId() == command.Target.GetEntityId() || command.Mode == nil || attackJobDef(command.GetMode()) == "" || command.RequireHostile == nil || command.RequireStanding == nil || command.RequireCombatHealth == nil {
		return contract("explicit melee or ranged guards and distinct targets required")
	}
	return nil
}
func attackAttempt(v AttackAttempt) (AttackAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return AttackAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return AttackAttempt{}, err
	}
	if v.NativeGeneration == 0 {
		return AttackAttempt{}, contract("attack admission generation mismatch")
	}
	if err := validID(v.PawnID); err != nil {
		return AttackAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	if validID(v.TargetID) != nil || v.TargetID == v.PawnID || attackJobDef(v.Mode) == "" {
		return AttackAttempt{}, contract("invalid melee attempt target or mode")
	}
	return v, nil
}

// PreviewAttack checks exact ordinary melee; acceptance is not authority.
func (client *Client) PreviewAttack(ctx context.Context, identity *c.Identity, command *o.AttackTarget) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := attackCommand(command); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	command = proto.Clone(command).(*o.AttackTarget)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: attackOperation(command)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("attack preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("attack preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(command.Pawn.GetEntityId()), JobDef: proto.String(attackJobDef(command.GetMode())), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: command.Target.GetEntityId()}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if !proto.Equal(job, expected) {
			err = contract("attack preview projection mismatch")
		}
	default:
		err = contract("attack preview outcome missing")
	}
	return reply, raw, err
}
func (control *AttackControl) AttackTarget(ctx context.Context, pre *a.WritePrecondition, command *o.AttackTarget) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("attack capability missing")
	}
	if pre == nil {
		return nil, Result{}, contract("attack precondition missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, Result{}, err
	}
	if err := attackCommand(command); err != nil {
		return nil, Result{}, err
	}
	expected, err := attackAttempt(AttackAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), command.Pawn.GetEntityId(), command.Target.GetEntityId(), command.GetMode(), command.GetRequireHostile(), command.GetRequireStanding(), command.GetRequireCombatHealth()})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	command = proto.Clone(command).(*o.AttackTarget)
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: attackOperation(command)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = attackReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("attack execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupAttackAttempt(ctx context.Context, attempt AttackAttempt) (*r.LookupReply, Result, error) {
	expected, err := attackAttempt(attempt)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = attackReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("attack in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.NativeGeneration, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("attack unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("attack lookup outcome missing")
	}
	return reply, raw, err
}

// ObserveAttackProgress preserves causal native outcomes, including completion before later Manual.
func (client *Client) ObserveAttackProgress(ctx context.Context, attempt AttackAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := attackAttempt(attempt)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = attackReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = attackProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("attack progress outcome missing")
	}
	return reply, raw, err
}
