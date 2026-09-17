package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewWithMovement(journal MovementJournal, building Boundary, draft DraftBoundary, move MovementBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if move == nil {
		return nil, errors.New("movement boundary required")
	}
	e, err := NewWithDraft(journal, building, draft, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.movementJournal, e.movement = journal, move
	return e, nil
}

// NewWithMeleeAndMovement composes melee attack and movement onto one
// draft-backed executor, mirroring NewWithMeleeAndRanged: a drafted pawn's
// plan may include either or both of a melee engagement and an explicit
// walk-to-cell order.
func NewWithMeleeAndMovement(journal interface {
	MeleeJournal
	MovementJournal
}, building Boundary, draft DraftBoundary, melee MeleeBoundary, move MovementBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if move == nil {
		return nil, errors.New("movement boundary required")
	}
	e, err := NewWithMelee(journal, building, draft, melee, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.movementJournal, e.movement = journal, move
	return e, nil
}

// NewWithRangedAndMovement composes ranged attack and movement onto one
// draft-backed executor, mirroring NewWithMeleeAndRanged.
func NewWithRangedAndMovement(journal interface {
	RangedJournal
	MovementJournal
}, building Boundary, draft DraftBoundary, ranged RangedBoundary, move MovementBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if move == nil {
		return nil, errors.New("movement boundary required")
	}
	e, err := NewWithRanged(journal, building, draft, ranged, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.movementJournal, e.movement = journal, move
	return e, nil
}

// NewWithMeleeRangedAndMovement composes all three drafted-pawn action
// families onto one executor, for a plan that may issue any mix of melee,
// ranged, and movement orders against the same owned draft.
func NewWithMeleeRangedAndMovement(journal interface {
	MeleeJournal
	RangedJournal
	MovementJournal
}, building Boundary, draft DraftBoundary, melee MeleeBoundary, ranged RangedBoundary, move MovementBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if move == nil {
		return nil, errors.New("movement boundary required")
	}
	e, err := NewWithMeleeAndRanged(journal, building, draft, melee, ranged, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.movementJournal, e.movement = journal, move
	return e, nil
}

func movementProgressLookup(state store.PlanState, id domain.ActionID) (domain.Progress, bool) {
	for _, p := range state.Progress {
		if p.View().Action == id {
			return p, true
		}
	}
	return domain.Progress{}, false
}

func (e *Executor) runMovement(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	m, ok := action.Movement()
	if !ok || progress.Action() != action {
		return result, ErrEvidence
	}
	v := progress.View()
	if v.Unresolved {
		return e.reconcileMovement(ctx, result, generation)
	}
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		// A routine method plan (hold-the-line) runs under the root
		// authority; guard re-authorizes it before every native call.
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	minimum := v.Tick
	var inspection MovementInspection
	var admission store.MovementAdmission
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		state, err := e.journal.LoadPlan(ctx, v.Plan)
		if err != nil {
			return result, err
		}
		latest, exists := movementProgressLookup(state, v.Action)
		if !exists || latest.Action() != action {
			return result, ErrEvidence
		}
		result.Progress = latest
		prerequisite, exists := movementProgressLookup(state, m.DraftAction())
		if !exists {
			return result, ErrEvidence
		}
		cleanup, known := prerequisite.View().DraftCleanup.Value()
		claim, claimed := cleanup.Claim.Value()
		if !known || !claimed || prerequisite.View().Stage != domain.Completed || prerequisite.View().Unresolved || cleanup.Stage != domain.DraftCleanupRequired || claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Origin != expected || prerequisite.View().Snapshot != expected {
			return result, ErrHeld
		}
		minimum = max(minimum, latest.View().Tick, prerequisite.View().Tick)
		for _, old := range state.MovementAdmissions {
			if old.Action == v.Action {
				minimum = max(minimum, old.Admission.Tick)
			}
		}
		inspection, err = e.movement.InspectMovement(ctx, Target{action, expected}, claim)
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		decision := policy.EvaluateMovement(policy.MovementRequest{Action: action, Progress: latest, DraftProgress: prerequisite, Current: expected, MinimumTick: minimum, Facts: inspection.Facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			return result, ErrHeld
		}
		facts := inspection.Facts
		admission = store.MovementAdmission{Snapshot: expected, Tick: facts.PreviewTick, Pawn: m.Pawn(), Destination: m.Destination(), PawnSnapshotToken: facts.Pawn.SnapshotToken, DraftClaim: claim}
		next, err := e.movementJournal.PrepareMovement(ctx, v.Plan, v.Action, admission)
		if err != nil {
			return result, err
		}
		result.Progress = next
		minimum = max(minimum, facts.PreviewTick)
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, admission.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, admission.Tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.movement.MoveTo(ctx, MovementDispatch{attempt, admission})
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}

func (e *Executor) reconcileMovement(ctx context.Context, result Result, generation context.Context) (Result, error) {
	p := result.Progress
	v := p.View()
	current := e.current().Snapshot
	if current.Validate() != nil || current.Native == 0 || current.Native < v.Snapshot.Native || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
		return result, ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, v.Plan)
	if err != nil {
		return result, err
	}
	var admission store.MovementAdmission
	found := false
	for _, record := range state.MovementAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	evidence, err := e.movement.ObserveMovement(ctx, MovementDispatch{draftAttempt(p.Action(), p), admission}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	m, _ := p.Action().Movement()
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || evidence.Pawn != m.Pawn() || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrEvidence
	}
	switch o.Effect {
	case domain.EffectCompleted, domain.EffectAbsent, domain.EffectUnsuccessful:
		if !evidence.Complete || o.Causality != domain.AfterDispatch {
			return result, ErrEvidence
		}
	case domain.EffectUnknown, domain.EffectPending:
	default:
		return result, ErrEvidence
	}
	next, err := e.journal.Observe(ctx, v.Plan, o, current)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
