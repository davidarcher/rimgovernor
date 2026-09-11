package bridge

import (
	"context"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func movementOperation(command *o.MovePawn) *o.Operation {
	return &o.Operation{Command: &o.Operation_MovePawn{MovePawn: command}}
}
func movementCommand(command *o.MovePawn) error {
	if command == nil {
		return contract("movement command missing")
	}
	if err := buildingUnknown(command); err != nil {
		return err
	}
	if err := draftEntity(command.Pawn); err != nil {
		return err
	}
	return movementCell(command.Destination)
}
func movementCell(cell *c.Cell) error {
	if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
		return contract("movement destination missing or invalid")
	}
	return buildingUnknown(cell)
}
func movementAttempt(v MovementAttempt) (MovementAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return MovementAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return MovementAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return MovementAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return MovementAttempt{}, err
	}
	if v.NativeGeneration == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return MovementAttempt{}, contract("movement admission owner or generation mismatch")
	}
	if err := validID(v.PawnID); err != nil {
		return MovementAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	if err := movementCell(v.Destination); err != nil {
		return MovementAttempt{}, err
	}
	v.Destination = proto.Clone(v.Destination).(*c.Cell)
	return v, nil
}

// PreviewMovement checks exact ordinary movement; acceptance is not authority.
func (client *Client) PreviewMovement(ctx context.Context, identity *c.Identity, command *o.MovePawn) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := movementCommand(command); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	command = proto.Clone(command).(*o.MovePawn)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: movementOperation(command)}, reply)
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
			return reply, raw, contract("movement preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("movement preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(command.Pawn.GetEntityId()), JobDef: proto.String("Goto"), TargetA: &r.JobTarget{Target: &r.JobTarget_Cell{Cell: command.Destination}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if !proto.Equal(job, expected) {
			err = contract("movement preview projection mismatch")
		}
	default:
		err = contract("movement preview outcome missing")
	}
	return reply, raw, err
}
func (control *MovementControl) MovePawn(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, command *o.MovePawn) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("movement capability missing")
	}
	if pre == nil || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("movement lease missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, Result{}, err
	}
	if err := movementCommand(command); err != nil {
		return nil, Result{}, err
	}
	expected, err := movementAttempt(MovementAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), owner, command.Pawn.GetEntityId(), command.Destination})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	command = proto.Clone(command).(*o.MovePawn)
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: movementOperation(command)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = movementReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("movement execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupMovementAttempt(ctx context.Context, attempt MovementAttempt) (*r.LookupReply, Result, error) {
	expected, err := movementAttempt(attempt)
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
		err = movementReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("movement in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.NativeGeneration, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("movement unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("movement lookup outcome missing")
	}
	return reply, raw, err
}

// ObserveMovementProgress requires correlated native arrival evidence, never just an accepted job.
func (client *Client) ObserveMovementProgress(ctx context.Context, attempt MovementAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := movementAttempt(attempt)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = movementReceipt(admitted, expected); err != nil {
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
		err = movementProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("movement progress outcome missing")
	}
	return reply, raw, err
}
