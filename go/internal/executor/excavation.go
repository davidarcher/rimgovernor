package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ExcavationInspection is one paused native read of the excavation cell,
// its counterfactual roof support and worker access, plus the emergency
// snapshot taken in the same tick.
type ExcavationInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Excavation            domain.Excavation
	SnapshotToken         string
	Facts                 policy.ExcavationFacts
	Emergency             policy.EmergencySnapshot
}
type ExcavationDispatch struct {
	Attempt       Placement
	SnapshotToken string
}

// ExcavationEvidence: Cleared is the only completion evidence; Designated
// means ordinary pawn mining is still pending; neither means the designation
// was cancelled or the native guard ended the job (Blocker).
type ExcavationEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Excavation            domain.Excavation
	Designated, Cleared   bool
	Cancelled             bool
	Blocker               string
}
type ExcavationBoundary interface {
	InspectExcavation(context.Context, Target) (ExcavationInspection, error)
	Excavate(context.Context, ExcavationDispatch) (Receipt, error)
	ObserveExcavation(context.Context, Placement, domain.GenerationSnapshot) (ExcavationEvidence, error)
}
type ExcavationJournal interface {
	Journal
	PrepareExcavation(context.Context, domain.PlanID, domain.ActionID, store.ExcavationAdmission) (domain.Progress, error)
}

func (e *Executor) EnableExcavation(excavation ExcavationBoundary) error {
	if excavation == nil {
		return errors.New("excavation boundary required")
	}
	j, ok := e.journal.(ExcavationJournal)
	if !ok {
		return errors.New("excavation boundary requires typed journal")
	}
	e.excavation, e.excavationJournal = excavation, j
	return nil
}

func (e *Executor) runExcavation(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	excavation, ok := action.Excavation()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.excavation.ObserveExcavation(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
		if !o.Snapshot.Matches(current) || o.Action != action.ID() || o.Attempt != v.Attempt || !e.fresh(evidence.StartedAt, evidence.ObservedAt) || o.Construction != nil || o.ConstructionObserved {
			return result, ErrEvidence
		}
		switch o.Effect {
		case domain.EffectCompleted:
			if !evidence.Complete || evidence.Excavation != excavation || !evidence.Cleared {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			if !evidence.Complete || evidence.Excavation != excavation || evidence.Cleared || evidence.Designated || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectAbsent:
			// The native ledger has no entry for the attempt: the dispatch
			// timed out before admission (#71), so the designation never ran
			// and the action returns to Pending for a fresh attempt (#165).
			// Only the boundary's complete post-dispatch lookup says so;
			// anything less cannot authorize a second designation.
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
	var inspection ExcavationInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.excavation.InspectExcavation(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Excavation != excavation || inspection.Facts.Snapshot != expected || inspection.Facts.ObservationTick != inspection.Tick || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		decision := policy.EvaluateExcavation(policy.ExcavationRequest{Action: action, Progress: result.Progress, Current: expected, Facts: inspection.Facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		if inspection.SnapshotToken == "" {
			return result, ErrHeld
		}
		next, err := e.excavationJournal.PrepareExcavation(ctx, v.Plan, v.Action, store.ExcavationAdmission{Snapshot: expected, Tick: inspection.Tick, Cell: excavation.Cell(), Definition: excavation.Definition(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.excavation.Excavate(ctx, ExcavationDispatch{attempt, inspection.SnapshotToken})
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
