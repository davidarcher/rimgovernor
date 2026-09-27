package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BuildingInspection anchors a building intent's dispatch to a native read
// of the current world and tick. It admits nothing: native validates the
// placement when it applies the intent (#856).
type BuildingInspection struct {
	StartedAt, ObservedAt time.Time
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
}

// runBuilding dispatches one building intent. The receipt is terminal:
// applied (the blueprint is placed, or a matching one already stood) is
// done, refused is over and the owning routine replans, and a lost reply
// is sent again under a new attempt.
func (e *Executor) runBuilding(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	if _, ok := action.Building(); !ok || p.Action() != action {
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
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	inspection, err := e.boundary.InspectBuilding(ctx, Target{action, expected})
	if err != nil {
		return result, err
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !inspection.Current.Matches(expected) || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	tick := max(v.Tick, inspection.Tick)
	next, err := e.journal.Prepare(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
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
	receipt, err := e.boundary.WriteBuilding(ctx, attempt)
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
