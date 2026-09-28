package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// NewWithMovement is New with explicit walk-to-cell orders for drafted
// pawns.
func NewWithMovement(journal MovementJournal, building Boundary, move MovementBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if move == nil {
		return nil, errors.New("movement boundary required")
	}
	e, err := New(journal, building, clock, limits, routine...)
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

// runMovement sends one move intent once its draft prerequisite has
// applied: guard, Prepare, Dispatch, DispatchIntent. Native validates
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
	if d.Stage != domain.Completed || d.Unresolved || !d.Snapshot.SameWorld(expected) {
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
