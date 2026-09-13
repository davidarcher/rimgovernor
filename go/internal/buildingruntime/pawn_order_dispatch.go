package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// PawnOrderWriter is the shared write surface for the "pawn order" boundaries
// (Tend, Haul, Rescue, Equip): every native writer for these already declares
// this exact OrderPawn signature, so no boundary-specific interface changes
// are needed to use it here.
type PawnOrderWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *a.Owner, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}

// dispatchPawnOrder runs the write half shared by every pawn-order boundary:
// build the attempt, lease it, issue OrderPawn, classify a refusal, and
// validate the resulting receipt/job. buildAttempt validates the
// dispatch-specific admission fields and produces the bridge attempt;
// buildCommand turns that attempt into the domain-specific *o.PawnTargetOrder;
// checkReceipt runs the domain-specific job validation after the generic
// receipt/tick checks pass. This is the one part of Tend/Haul/Rescue/Equip
// dispatch that carries no domain-specific variance.
func dispatchPawnOrder(ctx context.Context, leases LeaseSource, writer PawnOrderWriter, p executor.Placement,
	buildAttempt func() (bridge.PawnOrderAttempt, error),
	buildCommand func(bridge.PawnOrderAttempt) *o.PawnTargetOrder,
	checkReceipt func(*r.Receipt) error,
) (executor.Receipt, error) {
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := buildAttempt()
	if err != nil {
		return out, err
	}
	lease, err := leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundaryID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration), LeaseId: proto.String(lease)}
	reply, _, err := writer.OrderPawn(ctx, pre, attempt.Owner, buildCommand(attempt))
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = checkReceipt(receipt); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}
