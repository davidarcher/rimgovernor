package movement

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type MovementNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	PreviewMovement(context.Context, *c.Identity, *o.MovePawn) (*o.PreviewReply, bridge.Result, error)
	LookupMovementAttempt(context.Context, bridge.MovementAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveMovementProgress(context.Context, bridge.MovementAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type MovementWriter interface {
	MovePawn(context.Context, *a.WritePrecondition, *o.MovePawn) (*o.ExecuteReply, bridge.Result, error)
}
type MovementCapabilities struct {
	Native MovementNative
	Writer MovementWriter
}
type MovementBoundary struct {
	native  MovementNative
	writer  MovementWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewMovementBoundary(native MovementNative, writer MovementWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*MovementBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid movement boundary dependencies")
	}
	return &MovementBoundary{native, writer, leases, clock, session}, nil
}

func movementCommand(pawn, pawnToken string, destination domain.Cell) *o.MovePawn {
	return &o.MovePawn{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Destination: &c.Cell{X: proto.Int32(destination.X), Z: proto.Int32(destination.Z)}}
}

func movementClaim(action domain.Action, snapshot domain.GenerationSnapshot, claim domain.DraftClaim, session string) error {
	m, ok := action.Movement()
	if !ok || snapshot.Validate() != nil || snapshot.Native == 0 || snapshot.Direction == 0 || snapshot.Revision == 0 || claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Origin != snapshot || claim.Attempt == 0 || string(claim.Session) != session || !boundary.ValidID(string(claim.Claim)) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *MovementBoundary) pawnRead(ctx context.Context, pawn string, current domain.GenerationSnapshot) (*n.PawnState, *c.ObservationContext, error) {
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(current), []string{pawn})
	if err != nil {
		return nil, nil, err
	}
	v := reply.GetObserved()
	if v == nil {
		return nil, nil, executor.ErrHeld
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return nil, nil, err
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(v.Pawns)) || counts.GetReturned() != uint64(len(v.Pawns)) || len(v.Pawns) != 1 {
		return nil, nil, executor.ErrHeld
	}
	row := v.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != pawn {
		return nil, nil, executor.ErrEvidence
	}
	return row, v.Context, nil
}

func (b *MovementBoundary) InspectMovement(ctx context.Context, target executor.Target, claim domain.DraftClaim) (executor.MovementInspection, error) {
	out := executor.MovementInspection{StartedAt: b.clock.Now()}
	if err := movementClaim(target.Action, target.Snapshot, claim, b.session); err != nil {
		return out, err
	}
	m, _ := target.Action.Movement()
	row, observed, err := b.pawnRead(ctx, string(m.Pawn()), target.Snapshot)
	if err != nil {
		return out, err
	}
	token, err := boundary.PawnToken(row, observed)
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(observed, target.Snapshot)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewMovement(ctx, boundary.Identity(current), movementCommand(string(m.Pawn()), token, m.Destination()))
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < observed.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(m.Pawn()) || job.GetJobDef() != "Goto" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	emergency, _, err := b.native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < evaluated.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts := policy.MovementFacts{Snapshot: current, PawnTick: domain.Tick(observed.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Emergency, err = policy.NewEmergencySnapshot(current, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	facts.Pawn = policy.MovementPawnFacts{Pawn: m.Pawn(), SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), FreeColonist: boundary.FactBool(row.FreeColonist), Drafted: boundary.FactBool(row.Drafted)}
	if row.Health != nil {
		facts.Pawn.Bleeding, facts.Pawn.NeedsTend = boundary.FactBool(row.Health.Bleeding), boundary.FactBool(row.Health.NeedsTend)
	}
	// A claim is either held by the single bot process or it is not: native
	// no longer reports a distinct session/direction for it (see
	// observations.proto's OwnedDraftClaim), so an observed claim is by
	// construction ours in the current epoch.
	if owned := row.GetDraftClaim().GetOwned(); owned != nil && boundary.ValidID(owned.GetClaimId()) && owned.PawnSnapshot != nil && proto.Equal(owned.PawnSnapshot, row.Pawn.Snapshot) {
		facts.Pawn.Owner = domain.Known(policy.MovementDraftOwner{Claim: domain.DraftClaimID(owned.GetClaimId()), Session: domain.ControllerSessionID(b.session), Direction: current.Direction})
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *MovementBoundary) attempt(dispatch executor.MovementDispatch) (bridge.MovementAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	if err := movementClaim(p.Action, p.Snapshot, admission.DraftClaim, b.session); err != nil {
		return bridge.MovementAttempt{}, err
	}
	m, _ := p.Action.Movement()
	if p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != m.Pawn() || admission.Destination != m.Destination() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) {
		return bridge.MovementAttempt{}, executor.ErrEvidence
	}
	return bridge.MovementAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(m.Pawn()), Destination: &c.Cell{X: proto.Int32(m.Destination().X), Z: proto.Int32(m.Destination().Z)}}, nil
}

func movementJob(job *r.JobEffect, dispatch executor.MovementDispatch) error {
	if job == nil {
		return nil
	}
	m, _ := dispatch.Attempt.Action.Movement()
	cell := job.GetTargetA().GetCell()
	if job.GetPawnId() != string(m.Pawn()) || cell.GetX() != m.Destination().X || cell.GetZ() != m.Destination().Z || job.GetJobDef() != "Goto" {
		return executor.ErrEvidence
	}
	if job.DraftClaimId != nil && job.GetDraftClaimId() != string(dispatch.Admission.DraftClaim.Claim) || job.DraftOwner != nil && job.GetDraftOwner() != string(dispatch.Admission.DraftClaim.Session) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *MovementBoundary) MoveTo(ctx context.Context, dispatch executor.MovementDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration)}
	reply, _, err := b.writer.MovePawn(ctx, pre, movementCommand(attempt.PawnID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.Destination))
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

func (b *MovementBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.MovementDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return movementJob(job, dispatch)
}

var _ executor.MovementBoundary = (*MovementBoundary)(nil)
