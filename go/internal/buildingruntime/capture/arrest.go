package capture

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type arrestNative interface {
	PreviewArrest(context.Context, *c.Identity, *o.Arrest) (*o.PreviewReply, bridge.Result, error)
}
type arrestWriter interface {
	ArrestPawn(context.Context, *a.WritePrecondition, *o.Arrest) (*o.ExecuteReply, bridge.Result, error)
}

func arrestCommand(capture domain.Capture, pawnToken, targetToken string) *o.Arrest {
	return &o.Arrest{Pawn: &o.EntityPrecondition{EntityId: proto.String(string(capture.Capturer())), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(string(capture.Patient())), ExpectedSnapshotToken: proto.String(targetToken)}, Bed: &o.EntityPrecondition{EntityId: proto.String(capture.Bed())}}
}
func captureJobDef(capture domain.Capture) string {
	if capture.Arrest() {
		return "Arrest"
	}
	return "Capture"
}

func (b *CaptureBoundary) preview(ctx context.Context, current domain.GenerationSnapshot, capture domain.Capture, pawnToken, targetToken string) (*o.PreviewReply, error) {
	if capture.Arrest() {
		native, ok := b.native.(arrestNative)
		if !ok {
			return nil, executor.ErrHeld
		}
		reply, _, err := native.PreviewArrest(ctx, boundary.Identity(current), arrestCommand(capture, pawnToken, targetToken))
		return reply, err
	}
	reply, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), captureCommand(string(capture.Capturer()), string(capture.Patient()), pawnToken, targetToken))
	return reply, err
}

func (b *CaptureBoundary) arrest(ctx context.Context, dispatch executor.CaptureDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	writer, ok := b.writer.(arrestWriter)
	if !ok {
		return out, executor.ErrHeld
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	capture, _ := p.Action.Capture()
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration)}
	reply, _, err := writer.ArrestPawn(ctx, pre, arrestCommand(capture, dispatch.Admission.CapturerSnapshotToken, dispatch.Admission.PatientSnapshotToken))
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
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
