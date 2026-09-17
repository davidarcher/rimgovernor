// Package bedassign wires MaintainSleeping's bed ownership transfer: one
// exact pawn, one exact vacant bed and the pawn's expected previous bed,
// driven through the typed AssignBed operation
// (NativeBedAssignOperations.cs). It follows the bedmedical Settings-style
// shape (fresh CAS reads, native preview, then a direct
// write/lookup/observe) rather than the Owner/Attempt Job pattern of
// pawn-order boundaries: the game's TryAssignPawn is synchronous, so the
// receipt already carries the ownership readback and observation only
// confirms it.
package bedassign

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// Native reuses the generic pawn read for the pawn's CAS token and
// eligibility facts plus the exact-ID bed lookup for the bed's token; the
// upkeep census strips tokens from its rows, so both are refreshed
// immediately before dispatch.
type Native interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadBedTarget(context.Context, *c.Identity, string) (bridge.BedTarget, bridge.Result, error)
	PreviewBedAssign(context.Context, *c.Identity, string, string, string, string, bridge.BedAssignPreviousBed) (*op.PreviewReply, bridge.Result, error)
	LookupBedAssign(context.Context, bridge.BedAssignAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveBedAssignProgress(context.Context, bridge.BedAssignAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyBedAssign(context.Context, *a.WritePrecondition, string, string, string, string, bridge.BedAssignPreviousBed) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	assign Capabilities
}

func NewBoundary(base *boundary.Boundary, assign Capabilities) *Boundary {
	return &Boundary{Boundary: base, assign: assign}
}

func previousWire(previous domain.PreviousBed) bridge.BedAssignPreviousBed {
	if previous.Clear() {
		return bridge.BedAssignPreviousBed{Clear: true}
	}
	return bridge.BedAssignPreviousBed{ID: previous.ID()}
}

func (b *Boundary) InspectBedAssign(ctx context.Context, target executor.Target) (executor.BedAssignInspection, error) {
	out := executor.BedAssignInspection{StartedAt: b.Clock.Now()}
	assign, ok := target.Action.BedAssign()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.assign.Native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(assign.Pawn())})
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
	bed, _, err := b.assign.Native.ReadBedTarget(ctx, boundary.Identity(current), assign.Bed())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(bed.Context, current); err != nil {
		return out, err
	}
	if bed.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.assign.Native.PreviewBedAssign(ctx, boundary.Identity(current), string(assign.Pawn()), pawnToken, assign.Bed(), bed.Token, previousWire(assign.PreviousBed()))
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
	out.Facts, out.ObservedAt = facts, b.Clock.Now()
	return out, ctx.Err()
}

func admissionPrevious(admission store.BedAssignAdmission) (domain.PreviousBed, bool) {
	if admission.PreviousBedClear {
		return domain.ClearPreviousBed(), admission.PreviousBedID == ""
	}
	previous, err := domain.KnownPreviousBed(admission.PreviousBedID)
	return previous, err == nil
}

func (b *Boundary) attempt(dispatch executor.BedAssignDispatch) (bridge.BedAssignAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	assign, ok := p.Action.BedAssign()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != assign.Pawn() || admission.Bed != assign.Bed() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.BedSnapshotToken) {
		return bridge.BedAssignAttempt{}, executor.ErrEvidence
	}
	if previous, ok := admissionPrevious(admission); !ok || previous != assign.PreviousBed() {
		return bridge.BedAssignAttempt{}, executor.ErrEvidence
	}
	return bridge.BedAssignAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Pawn: string(assign.Pawn()), Bed: assign.Bed(), PawnToken: admission.PawnSnapshotToken, BedToken: admission.BedSnapshotToken, Previous: previousWire(assign.PreviousBed())}, nil
}

func (b *Boundary) AssignBedPawn(ctx context.Context, dispatch executor.BedAssignDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	attempt, err := b.attempt(dispatch)
	return b.DispatchWrite(ctx, p,
		func() error { return err },
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			reply, raw, err := b.assign.Writer.ApplyBedAssign(ctx, pre, attempt.Pawn, attempt.PawnToken, attempt.Bed, attempt.BedToken, attempt.Previous)
			if err == nil && reply.GetReceipt().GetAdmittedContext().GetTick() < int64(dispatch.Admission.Tick) {
				return nil, raw, executor.ErrEvidence
			}
			return reply, raw, err
		},
	)
}

func (b *Boundary) ObserveBedAssign(ctx context.Context, dispatch executor.BedAssignDispatch, current domain.GenerationSnapshot) (executor.BedAssignEvidence, error) {
	p := dispatch.Attempt
	out := executor.BedAssignEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lookup, _, err := b.assign.Native.LookupBedAssign(ctx, attempt)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		absent, err := boundary.Unadmitted(lookup, p, current)
		if err != nil {
			return out, err
		}
		out.Observation, out.Complete, out.Pawn, out.Bed, out.ObservedAt = absent, true, domain.PawnID(attempt.Pawn), attempt.Bed, b.Clock.Now()
		return out, nil
	}
	if err = boundary.Admission(admitted, p, b.Session); err != nil {
		return out, err
	}
	if admitted.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.assign.Native.ObserveBedAssignProgress(ctx, attempt, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	out.Pawn, out.Bed, out.ObservedAt = domain.PawnID(attempt.Pawn), attempt.Bed, b.Clock.Now()
	switch v.Effect.(type) {
	case *r.Progress_Unknown:
	case *r.Progress_Pending:
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Completed:
		out.Observation.Effect, out.Complete = domain.EffectCompleted, true
	case *r.Progress_Absent:
		out.Observation.Effect, out.Complete = domain.EffectAbsent, true
	case *r.Progress_Unsuccessful:
		out.Observation.Effect, out.Observation.UnsuccessfulReason, out.Complete = domain.EffectUnsuccessful, domain.OutcomeNotAchieved, true
	default:
		return out, executor.ErrEvidence
	}
	if out.Complete && !v.GetCompleteInspection() {
		return out, executor.ErrHeld
	}
	return out, ctx.Err()
}

var _ executor.BedAssignBoundary = (*Boundary)(nil)
