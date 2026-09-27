package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func NewWithMelee(journal MeleeJournal, building Boundary, draft DraftBoundary, melee MeleeBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if melee == nil {
		return nil, errors.New("melee boundary required")
	}
	e, err := NewWithDraft(journal, building, draft, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.meleeJournal, e.melee = journal, melee
	return e, nil
}

// runMelee sends a melee attack or subdue as an intent (#856) once its owned
// draft is held. Native validates the pawn, target and draft live when it
// applies; the receipt is terminal.
func (e *Executor) runMelee(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	m, ok := action.MeleeAttack()
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
	var draft domain.ProgressView
	for _, p := range state.Progress {
		if p.View().Action == m.DraftAction() {
			draft = p.View()
		}
	}
	if cleanup, known := draft.DraftCleanup.Value(); !known || draft.Stage != domain.Completed || draft.Unresolved || cleanup.Stage != domain.DraftCleanupRequired {
		return result, ErrHeld
	}
	tick := max(v.Tick, draft.Tick)
	if v.Stage == domain.Pending || !v.Snapshot.Matches(expected) {
		if result.Progress, err = e.meleeJournal.Prepare(ctx, v.Plan, v.Action, expected, tick); err != nil {
			return result, err
		}
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, tick}
	result.NativeCalled = true
	receipt, err := e.melee.WriteMelee(ctx, attempt)
	kind := receipt.Kind
	if err != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}
