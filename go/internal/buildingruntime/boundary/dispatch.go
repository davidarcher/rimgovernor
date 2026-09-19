package boundary

import (
	"context"
	"errors"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// PawnOrderWriter is the shared write surface for the "pawn order" boundaries
// (Tend, Haul, Rescue, Equip, Ranged, Melee): every native writer for these
// already declares this exact OrderPawn signature, so no boundary-specific
// interface changes are needed to use it here.
type PawnOrderWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}

// DispatchPawnOrder runs the write half shared by every pawn-order boundary:
// build the attempt, lease it, issue OrderPawn, classify a refusal, and
// validate the resulting receipt/job. buildAttempt validates the
// dispatch-specific admission fields and produces the bridge attempt;
// buildCommand turns that attempt into the domain-specific *o.PawnTargetOrder;
// checkReceipt runs the domain-specific job validation after the generic
// receipt/tick checks pass. This is the one part of Tend/Haul/Rescue/Equip
// dispatch that carries no domain-specific variance.
func DispatchPawnOrder(ctx context.Context, leases LeaseSource, writer PawnOrderWriter, p executor.Placement,
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
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration)}
	reply, _, err := writer.OrderPawn(ctx, pre, buildCommand(attempt))
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		// Native refusals are a receipt kind, not an error; surface the
		// native detail for harness diagnosis without changing the receipt.
		slog.Default().Debug("pawn order refused", telemetry.ComponentKey, "pawn-order", "action", string(p.Action.ID()), "order_kind", attempt.Kind.String(), "code", refused.Value.GetCode().String(), "detail", refused.Value.GetDetail())
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

// DispatchWrite runs the write half shared by every direct-write boundary
// (Bill, Work, Zone, Supply, Acquisition): validate, lease, issue the single
// write call, and classify the resulting receipt. validate does the
// domain-specific action extraction and any pre-write token check (Bill and
// Work reject a stale SnapshotToken here; Zone/Supply/Acquisition don't, since
// their target's token travels inside the write call itself and is checked
// natively). write receives the built precondition and performs the actual
// writer call.
func (b *Boundary) DispatchWrite(ctx context.Context, p executor.Placement, validate func() error, write func(*a.WritePrecondition) (*o.ExecuteReply, bridge.Result, error)) (executor.Receipt, error) {
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	if err := validate(); err != nil {
		return out, err
	}
	lease, err := b.Leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	pre := &a.WritePrecondition{Identity: Identity(p.Snapshot), Attempt: b.Attempt(p), ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native))}
	reply, _, err := write(pre)
	if err != nil {
		return out, err
	} // Refusals remain uncertain until correlated inspection.
	if err = Admission(reply.GetReceipt(), p, b.Session); err != nil {
		return out, err
	}
	if reply.GetReceipt().GetApplied() != nil {
		out.Kind = domain.ReceiptAccepted
	}
	return out, nil
}
