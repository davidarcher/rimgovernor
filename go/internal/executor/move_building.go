package executor

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

// The move family (#808) is the cut-plant shape on one exact installed
// building: inspect (native Reinstall preview + emergency), record the
// admission, dispatch the reinstall blueprint, observe until the building
// is installed at the destination (completed), still queued (pending) or
// the blueprint is gone without the move (unsuccessful).
type MoveBuildingJournal interface {
	Journal
	PrepareMoveBuilding(context.Context, domain.PlanID, domain.ActionID, store.MoveBuildingAdmission) (domain.Progress, error)
}

// ErrMoveBuildingAbsent reports a fresh preview that no longer finds the
// exact installed building (NotFound). The proposal can never succeed, so
// the executor cancels it.
var ErrMoveBuildingAbsent = errors.New("move building target absent")

type MoveBuildingInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Move                  domain.MoveBuilding
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type MoveBuildingDispatch struct {
	Attempt Placement
}
type MoveBuildingEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Move                  domain.MoveBuilding
	// Installed is the building observed at the destination cell and
	// rotation; completion requires it true, unsuccessful requires false.
	Installed domain.Fact[bool]
}
type MoveBuildingBoundary interface {
	InspectMoveBuilding(context.Context, Target) (MoveBuildingInspection, error)
	ApplyMoveBuilding(context.Context, MoveBuildingDispatch) (Receipt, error)
	ObserveMoveBuilding(context.Context, Placement, domain.GenerationSnapshot) (MoveBuildingEvidence, error)
}

// EnableMoveBuilding activates the move capability; see EnableAcquisition
// for why capabilities are wired this way instead of inferred from a
// composed Boundary.
func (e *Executor) EnableMoveBuilding(moveBuilding MoveBuildingBoundary) error {
	if moveBuilding == nil {
		return errors.New("move building boundary required")
	}
	j, ok := e.journal.(MoveBuildingJournal)
	if !ok {
		return errors.New("move building boundary requires typed journal")
	}
	e.moveBuilding, e.moveBuildingJournal = moveBuilding, j
	return nil
}

func (e *Executor) runMoveBuilding(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	move, _, ok := action.Relocation()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.moveBuilding.ObserveMoveBuilding(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
		if err != nil {
			return result, err
		}
		if ctx.Err() != nil {
			return result, ctx.Err()
		}
		if generation.Err() != nil || !e.current().Snapshot.Matches(current) {
			return result, ErrAuthority
		}
		o := evidence.Observation
		if !o.Snapshot.Matches(current) || o.Action != action.ID() || o.Attempt != v.Attempt || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
			return result, ErrEvidence
		}
		switch o.Effect {
		case domain.EffectCompleted:
			installed, known := evidence.Installed.Value()
			if !evidence.Complete || evidence.Move != move || !known || !installed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			installed, known := evidence.Installed.Value()
			if !evidence.Complete || evidence.Move != move || !known || installed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectAbsent:
			// The native ledger has no entry for the attempt: the dispatch
			// timed out before admission (#71), so no blueprint was placed
			// and the action returns to Pending for a fresh attempt. Only
			// the boundary's complete post-dispatch lookup says so.
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
	var inspection MoveBuildingInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.moveBuilding.InspectMoveBuilding(ctx, Target{action, expected})
		if errors.Is(err, ErrMoveBuildingAbsent) {
			// Settle the proposal so the plan closes and the planner
			// re-proposes from the next review.
			next, err := e.journal.Cancel(ctx, v.Plan, v.Action)
			if err != nil {
				return result, err
			}
			result.Progress = next
			return result, fmt.Errorf("%w: move building target absent, action cancelled", ErrHeld)
		}
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Move != move || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.moveBuildingJournal.PrepareMoveBuilding(ctx, v.Plan, v.Action, store.MoveBuildingAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: move.Thing()})
		if err != nil {
			return result, err
		}
		result.Progress = next
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.moveBuilding.ApplyMoveBuilding(ctx, MoveBuildingDispatch{attempt})
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != action.ID() || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}
