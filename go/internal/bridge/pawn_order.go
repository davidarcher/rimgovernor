package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func pawnOrderOperation(command *o.PawnTargetOrder) *o.Operation {
	return &o.Operation{Command: &o.Operation_PawnTargetOrder{PawnTargetOrder: command}}
}
func pawnOrderCommand(command *o.PawnTargetOrder) error {
	if command == nil {
		return contract("pawn order command missing")
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
	if command.Pawn.GetEntityId() == command.Target.GetEntityId() || command.Kind == nil || len(pawnOrderJobDefs(command.GetKind())) == 0 || command.RequireSafeStorage == nil || command.GetRequireSafeStorage() != pawnOrderRequiresSafeStorage(command.GetKind()) {
		return contract("supported undrafted pawn order kind required")
	}
	return nil
}
func pawnOrderAttempt(v PawnOrderAttempt) (PawnOrderAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return PawnOrderAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return PawnOrderAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return PawnOrderAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return PawnOrderAttempt{}, err
	}
	if v.NativeGeneration == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return PawnOrderAttempt{}, contract("pawn order admission owner or generation mismatch")
	}
	if err := validID(v.PawnID); err != nil {
		return PawnOrderAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	if validID(v.TargetID) != nil || v.TargetID == v.PawnID || len(pawnOrderJobDefs(v.Kind)) == 0 || v.RequireSafeStorage != pawnOrderRequiresSafeStorage(v.Kind) {
		return PawnOrderAttempt{}, contract("invalid pawn order attempt target or kind")
	}
	return v, nil
}

// PreviewPawnOrder checks an exact undrafted tend/rescue order; acceptance is
// not authority.
func (client *Client) PreviewPawnOrder(ctx context.Context, identity *c.Identity, command *o.PawnTargetOrder) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := pawnOrderCommand(command); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	command = proto.Clone(command).(*o.PawnTargetOrder)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: pawnOrderOperation(command)}, reply)
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
			return reply, raw, contract("pawn order preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("pawn order preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(command.Pawn.GetEntityId()), JobDef: job.JobDef, TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: command.Target.GetEntityId()}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if job.JobDef == nil || !pawnOrderJobDefAllowed(command.GetKind(), job.GetJobDef()) || !proto.Equal(job, expected) {
			err = contract("pawn order preview projection mismatch")
		}
	default:
		err = contract("pawn order preview outcome missing")
	}
	return reply, raw, err
}
func (control *PawnOrderControl) OrderPawn(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, command *o.PawnTargetOrder) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("pawn order capability missing")
	}
	if pre == nil || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("pawn order lease missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, Result{}, err
	}
	if err := pawnOrderCommand(command); err != nil {
		return nil, Result{}, err
	}
	expected, err := pawnOrderAttempt(PawnOrderAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), owner, command.Pawn.GetEntityId(), command.Target.GetEntityId(), command.GetKind(), command.GetRequireSafeStorage()})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	command = proto.Clone(command).(*o.PawnTargetOrder)
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: pawnOrderOperation(command)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = pawnOrderReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("pawn order execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupPawnOrderAttempt(ctx context.Context, attempt PawnOrderAttempt) (*r.LookupReply, Result, error) {
	expected, err := pawnOrderAttempt(attempt)
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
		err = pawnOrderReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("pawn order in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.NativeGeneration, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("pawn order unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("pawn order lookup outcome missing")
	}
	return reply, raw, err
}

// ObservePawnOrderProgress preserves causal native outcomes, including
// completion before later Manual.
func (client *Client) ObservePawnOrderProgress(ctx context.Context, attempt PawnOrderAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := pawnOrderAttempt(attempt)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = pawnOrderReceipt(admitted, expected); err != nil {
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
		err = pawnOrderProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("pawn order progress outcome missing")
	}
	return reply, raw, err
}
