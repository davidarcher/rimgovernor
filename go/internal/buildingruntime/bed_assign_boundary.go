package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// bedAssignPreviousBedWire converts the domain-level previous-bed expectation
// to the bridge's wire-facing shape, kept in sync by convention the same way
// recoveryServiceMethodWire tracks domain.RecoveryMethod.
func bedAssignPreviousBedWire(previous domain.PreviousBed) bridge.BedAssignPreviousBed {
	if previous.Clear() {
		return bridge.BedAssignPreviousBed{Clear: true}
	}
	return bridge.BedAssignPreviousBed{ID: previous.ID()}
}

// BedAssignNative reuses the generic bridge.ReadPawns for pawn eligibility
// facts plus the existing bridge.ReadBedTarget for the bed's CAS token, the
// same scoped refresh Repair's PawnOrder path uses; no dedicated bed-assign
// read is needed to plan a target, only to refresh CAS tokens immediately
// before dispatch.
type BedAssignNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadBedTarget(context.Context, *c.Identity, string) (bridge.BedTarget, bridge.Result, error)
	PreviewBedAssign(context.Context, *c.Identity, string, string, string, string, bridge.BedAssignPreviousBed) (*o.PreviewReply, bridge.Result, error)
	LookupBedAssign(context.Context, bridge.BedAssignAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveBedAssignProgress(context.Context, bridge.BedAssignAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type BedAssignWriter interface {
	ApplyBedAssign(context.Context, *a.WritePrecondition, *a.Owner, string, string, string, string, bridge.BedAssignPreviousBed) (*o.ExecuteReply, bridge.Result, error)
}
type BedAssignCapabilities struct {
	Native BedAssignNative
	Writer BedAssignWriter
}
type BedAssignBoundary struct {
	native  BedAssignNative
	writer  BedAssignWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewBedAssignBoundary(native BedAssignNative, writer BedAssignWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*BedAssignBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid bed assign boundary dependencies")
	}
	return &BedAssignBoundary{native, writer, leases, clock, session}, nil
}

func (b *BedAssignBoundary) InspectBedAssign(ctx context.Context, target executor.Target) (executor.BedAssignInspection, error) {
	out := executor.BedAssignInspection{StartedAt: b.clock.Now()}
	assign, ok := target.Action.BedAssign()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(assign.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
		return out, executor.ErrHeld
	}
	row := observed.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(assign.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	bed, _, err := b.native.ReadBedTarget(ctx, boundary.Identity(current), assign.Bed())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(bed.Context, current); err != nil {
		return out, err
	}
	if bed.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	previous := bedAssignPreviousBedWire(assign.PreviousBed())
	preview, _, err := b.native.PreviewBedAssign(ctx, boundary.Identity(current), string(assign.Pawn()), pawnToken, assign.Bed(), bed.Token, previous)
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
	effect := evaluated.GetProjected().GetBed()
	if evaluated.Context.GetTick() < bed.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.GetPawnId() != string(assign.Pawn()) || effect.GetBedId() != assign.Bed() {
		return out, executor.ErrEvidence
	}
	facts := policy.BedAssignFacts{Snapshot: current, PawnTick: domain.Tick(bed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), BedSnapshotToken: bed.Token, NativeCanTry: boundary.FactBool(evaluated.Accepted)}
	facts.Pawn = policy.BedAssignPawnFacts{Pawn: assign.Pawn(), SnapshotToken: pawnToken, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed)}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *BedAssignBoundary) attempt(dispatch executor.BedAssignDispatch) (bridge.BedAssignAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	assign, ok := p.Action.BedAssign()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != assign.Pawn() || admission.Bed != assign.Bed() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.BedSnapshotToken) {
		return bridge.BedAssignAttempt{}, executor.ErrEvidence
	}
	previous, err := bedAssignAdmissionPreviousBed(admission)
	if err != nil || previous != assign.PreviousBed() {
		return bridge.BedAssignAttempt{}, executor.ErrEvidence
	}
	return bridge.BedAssignAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Pawn: string(assign.Pawn()), Bed: assign.Bed(), PawnToken: admission.PawnSnapshotToken, BedToken: admission.BedSnapshotToken, Previous: bedAssignPreviousBedWire(assign.PreviousBed())}, nil
}

func (b *BedAssignBoundary) AssignBedPawn(ctx context.Context, dispatch executor.BedAssignDispatch) (executor.Receipt, error) {
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyBedAssign(ctx, pre, attempt.Owner, attempt.Pawn, attempt.PawnToken, attempt.Bed, attempt.BedToken, attempt.Previous)
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

func (b *BedAssignBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.BedAssignDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *BedAssignBoundary) ObserveBedAssign(ctx context.Context, dispatch executor.BedAssignDispatch, current domain.GenerationSnapshot) (executor.BedAssignEvidence, error) {
	out := executor.BedAssignEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupBedAssign(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext, AuthorizingOwner: attempt.Owner}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveBedAssignProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundary.Context(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	assign := mustBedAssign(dispatch)
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Pawn, out.Bed = assign.Pawn(), assign.Bed()
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Bed = true, assign.Pawn(), assign.Bed()
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Bed = true, assign.Pawn(), assign.Bed()
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Bed = true, assign.Pawn(), assign.Bed()
		return out, ctx.Err()
	default:
		_ = outcome
		return out, executor.ErrEvidence
	}
}

// bedAssignAdmissionPreviousBed reconstructs the domain-level previous-bed
// expectation from the store admission's exported fields; store.BedAssignAdmission
// keeps its own equivalent conversion private since it only needs it for
// admission validation, not for boundary attempt reconstruction.
func bedAssignAdmissionPreviousBed(admission store.BedAssignAdmission) (domain.PreviousBed, error) {
	if admission.PreviousBedClear {
		if admission.PreviousBedID != "" {
			return domain.PreviousBed{}, errors.New("cleared previous bed carries an identity")
		}
		return domain.ClearPreviousBed(), nil
	}
	return domain.KnownPreviousBed(admission.PreviousBedID)
}

func mustBedAssign(dispatch executor.BedAssignDispatch) domain.BedAssign {
	assign, _ := dispatch.Attempt.Action.BedAssign()
	return assign
}

var _ executor.BedAssignBoundary = (*BedAssignBoundary)(nil)
