package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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

// runMovement sends one move intent once its draft prerequisite holds a
// live claim: guard, Prepare, Dispatch, DispatchIntent. Native validates
// the move against live state when it applies; the receipt settles it.
func (e *Executor) runMovement(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	m, ok := action.Movement()
	if !ok || progress.Action() != action {
		return result, ErrEvidence
	}
	v := progress.View()
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		// A routine method plan runs under the root authority; guard
		// re-authorizes it before every native call.
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
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
	d := prerequisite.View()
	cleanup, known := d.DraftCleanup.Value()
	if !known || d.Stage != domain.Completed || d.Unresolved || cleanup.Stage != domain.DraftCleanupRequired || !d.Snapshot.SameWorld(expected) {
		return result, ErrHeld
	}
	tick := max(latest.View().Tick, d.Tick)
	next, err := e.movementJournal.Prepare(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	next, err = e.journal.Dispatch(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	result.NativeCalled = true
	receipt, err := e.movement.WriteMovement(ctx, attempt)
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
