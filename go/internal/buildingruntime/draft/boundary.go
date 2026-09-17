package draft

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type DraftNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewDraft(context.Context, *c.Identity, *o.EntityPrecondition) (*o.PreviewReply, bridge.Result, error)
	LookupDraftAttempt(context.Context, bridge.DraftAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveDraftProgress(context.Context, bridge.DraftAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type DraftWriter interface {
	DraftPawn(context.Context, *a.WritePrecondition, *o.EntityPrecondition) (*o.ExecuteReply, bridge.Result, error)
}
type DraftCleanupWriter interface {
	ReleaseOwnedDraft(context.Context, *o.ReleaseOwnedDraftRequest) (*o.ReleaseOwnedDraftReply, bridge.Result, error)
}
type DraftBoundary struct {
	native  DraftNative
	writer  DraftWriter
	cleanup DraftCleanupWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

var _ executor.DraftBoundary = (*DraftBoundary)(nil)

func NewDraftBoundary(native DraftNative, writer DraftWriter, cleanup DraftCleanupWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*DraftBoundary, error) {
	if native == nil || writer == nil || cleanup == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid draft boundary dependencies")
	}
	return &DraftBoundary{native, writer, cleanup, leases, clock, session}, nil
}
func draftPawn(action domain.Action, snapshot domain.GenerationSnapshot) (string, error) {
	d, ok := action.OwnedDraft()
	if !ok || snapshot.Validate() != nil || snapshot.Native == 0 || snapshot.Revision == 0 || !boundary.ValidID(string(action.ID())) || !boundary.ValidID(string(d.Pawn())) {
		return "", executor.ErrEvidence
	}
	return string(d.Pawn()), nil
}
func (b *DraftBoundary) attempt(p executor.Placement) (bridge.DraftAttempt, error) {
	pawn, err := draftPawn(p.Action, p.Snapshot)
	if err != nil || p.Attempt == 0 || p.Tick < 0 {
		return bridge.DraftAttempt{}, executor.ErrEvidence
	}
	return bridge.DraftAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: pawn}, nil
}

// pawnRead requires the requested whole-query result; no row is not absence proof.
func (b *DraftBoundary) pawnRead(ctx context.Context, pawn string, current domain.GenerationSnapshot) (*n.PawnState, *c.ObservationContext, error) {
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(current), []string{pawn})
	if err != nil {
		return nil, nil, err
	}
	v := reply.GetObserved()
	if v == nil {
		return nil, nil, fmt.Errorf("%w: pawn %s not observed", executor.ErrHeld, pawn)
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return nil, nil, err
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(v.Pawns)) || counts.GetReturned() != uint64(len(v.Pawns)) || len(v.Pawns) != 1 {
		return nil, nil, fmt.Errorf("%w: pawn %s read incomplete (%d rows)", executor.ErrHeld, pawn, len(v.Pawns))
	}
	row := v.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != pawn {
		return nil, nil, executor.ErrEvidence
	}
	return row, v.Context, nil
}
func (b *DraftBoundary) InspectDraft(ctx context.Context, target executor.Target) (executor.DraftInspection, error) {
	out := executor.DraftInspection{StartedAt: b.clock.Now()}
	if err := ctx.Err(); err != nil {
		return out, err
	}
	pawn, err := draftPawn(target.Action, target.Snapshot)
	if err != nil {
		return out, err
	}
	row, observed, err := b.pawnRead(ctx, pawn, target.Snapshot)
	if err != nil {
		return out, err
	}
	token, err := boundary.PawnToken(row, observed)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewDraft(ctx, boundary.Identity(target.Snapshot), &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(token)})
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, fmt.Errorf("%w: draft preview for %s not evaluated", executor.ErrHeld, pawn)
	}
	if _, err = boundary.Context(evaluated.Context, target.Snapshot); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < observed.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != pawn || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(emergency.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < evaluated.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	out.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	out.Current = current
	out.Tick = domain.Tick(evaluated.Context.GetTick())
	out.PawnSnapshotToken = token
	out.Pawn = policy.DraftPawnFacts{Pawn: domain.PawnID(pawn), Drafted: boundary.FactBool(row.Drafted), NativeCanTry: boundary.FactBool(job.CanTry)}
	if row.DraftClaim != nil {
		switch row.DraftClaim.State.(type) {
		case *n.DraftClaimObservation_Unowned:
			out.Pawn.Unowned = domain.Known(true)
		case *n.DraftClaimObservation_Owned:
			out.Pawn.Unowned = domain.Known(false)
		}
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}
func (b *DraftBoundary) claim(p executor.Placement, job *r.JobEffect, row *n.PawnState, observed *c.ObservationContext) (domain.Fact[domain.DraftClaim], error) {
	if job == nil || job.DraftClaimId == nil {
		return domain.Unknown[domain.DraftClaim](), nil
	}
	pawn, _ := p.Action.OwnedDraft()
	if job.GetPawnId() != string(pawn.Pawn()) || job.GetDraftOwner() != b.session || !boundary.ValidID(job.GetDraftClaimId()) {
		return domain.Unknown[domain.DraftClaim](), executor.ErrEvidence
	}
	owned := row.GetDraftClaim().GetOwned()
	if owned == nil || owned.GetClaimId() != job.GetDraftClaimId() {
		return domain.Unknown[domain.DraftClaim](), executor.ErrHeld
	}
	if _, err := boundary.PawnToken(row, observed); err != nil {
		return domain.Unknown[domain.DraftClaim](), err
	}
	if !proto.Equal(owned.PawnSnapshot, row.Pawn.Snapshot) || row.Drafted == nil || !row.GetDrafted() {
		return domain.Unknown[domain.DraftClaim](), executor.ErrHeld
	}
	return domain.Known(domain.DraftClaim{Action: p.Action.ID(), Attempt: p.Attempt, Pawn: pawn.Pawn(), Claim: domain.DraftClaimID(owned.GetClaimId()), Session: domain.ControllerSessionID(b.session), Origin: p.Snapshot}), nil
}
func (b *DraftBoundary) Draft(ctx context.Context, dispatch executor.DraftDispatch) (executor.DraftReceipt, error) {
	p := dispatch.Attempt
	out := executor.DraftReceipt{Receipt: executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}}
	attempt, err := b.attempt(p)
	if err != nil || !boundary.ValidID(dispatch.PawnSnapshotToken) {
		return out, executor.ErrEvidence
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration), Attempt: attempt.Attempt}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	reply, _, err := b.writer.DraftPawn(ctx, pre, &o.EntityPrecondition{EntityId: proto.String(attempt.PawnID), ExpectedSnapshotToken: proto.String(dispatch.PawnSnapshotToken)})
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Receipt.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = boundary.Admission(receipt, p, b.session); err != nil {
		return out, err
	}
	if receipt.AdmittedContext.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied, *r.Receipt_NoChange:
		out.Receipt.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	if job := boundary.ReceiptJob(receipt); job != nil && job.DraftClaimId != nil {
		row, observed, e := b.pawnRead(ctx, attempt.PawnID, p.Snapshot)
		if e != nil {
			return out, e
		}
		if observed.GetTick() < receipt.AdmittedContext.GetTick() {
			return out, executor.ErrEvidence
		}
		out.Claim, err = b.claim(p, job, row, observed)
		if err != nil {
			return out, err
		}
	}
	return out, ctx.Err()
}
