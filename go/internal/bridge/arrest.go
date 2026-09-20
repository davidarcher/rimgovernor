package bridge

import (
	"context"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func arrestCommand(command *o.Arrest) error {
	if command == nil {
		return contract("arrest command missing")
	}
	if err := buildingUnknown(command); err != nil {
		return err
	}
	for _, entity := range []*o.EntityPrecondition{command.Pawn, command.Target, command.Bed} {
		if entity == nil || validID(entity.GetEntityId()) != nil || entity.ExpectedSnapshotToken != nil && validID(entity.GetExpectedSnapshotToken()) != nil {
			return contract("invalid arrest entity precondition")
		}
	}
	if command.Pawn.GetEntityId() == command.Target.GetEntityId() {
		return contract("arrest target is its performer")
	}
	return nil
}

// PreviewArrest uses #487's operation, never a second PawnTargetOrder kind.
func (client *Client) PreviewArrest(ctx context.Context, identity *c.Identity, command *o.Arrest) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := arrestCommand(command); err != nil {
		return nil, Result{}, err
	}
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: &o.Operation{Command: &o.Operation_Arrest{Arrest: command}}}, reply)
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
		if v.Evaluated == nil || v.Evaluated.Accepted == nil || v.Evaluated.Projected != nil || v.Evaluated.Preparation != nil {
			return reply, raw, contract("invalid arrest preview")
		}
		err = buildingContext(v.Evaluated.Context, identity, 0, false)
	default:
		err = contract("arrest preview outcome missing")
	}
	return reply, raw, err
}

func (control *PawnOrderControl) ArrestPawn(ctx context.Context, pre *a.WritePrecondition, command *o.Arrest) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil || pre == nil {
		return nil, Result{}, contract("arrest capability or precondition missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, Result{}, err
	}
	if err := arrestCommand(command); err != nil {
		return nil, Result{}, err
	}
	expected, err := pawnOrderAttempt(PawnOrderAttempt{Identity: pre.Identity, Attempt: pre.Attempt, NativeGeneration: pre.GetExpectedGeneration(), PawnID: command.Pawn.GetEntityId(), TargetID: command.Target.GetEntityId(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE, ArrestBed: command.Bed.GetEntityId()})
	if err != nil {
		return nil, Result{}, err
	}
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: &o.Operation{Command: &o.Operation_Arrest{Arrest: proto.Clone(command).(*o.Arrest)}}}, reply)
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
		err = contract("arrest execute outcome missing")
	}
	return reply, raw, err
}
