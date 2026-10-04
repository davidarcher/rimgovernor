package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// runHaul sends one haul intent (#856). The only read is the tick the
// dispatch is journaled at; native checks the pawn and the item when it
// applies, and the receipt settles the attempt.
func (e *Executor) runHaul(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	if _, ok := action.Haul(); !ok || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		if e.roundsScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	inspection, err := e.haul.InspectHaul(ctx, Target{action, expected})
	if err != nil {
		return result, err
	}
	if inspection.Snapshot != expected {
		return result, ErrHeld
	}
	tick := max(v.Tick, inspection.Tick)
	next, err := e.haulJournal.Prepare(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	if next, err = e.journal.Dispatch(ctx, v.Plan, v.Action, expected, tick); err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	result.NativeCalled = true
	receipt, err := e.haul.WriteHaul(ctx, attempt)
	kind := receipt.Kind
	if err != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}
