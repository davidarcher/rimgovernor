package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// MineAcquisitionJournal is AcquisitionJournal's counterpart for the second,
// independently-registered mine-acquisition vertical (see
// domain.MineAcquisitionAction): its own typed admission persistence, keyed
// by its own store table rather than acquisition_admissions, since
// store.AcquisitionAdmission's validation is gated on action.Acquisition()
// (kind AcquisitionAction) and can never accept a MineAcquisitionAction.
type MineAcquisitionJournal interface {
	Journal
	PrepareMineAcquisition(context.Context, domain.PlanID, domain.ActionID, store.MineAcquisitionAdmission) (domain.Progress, error)
}

// EnableMineAcquisition activates the second, independently-registered
// mine-acquisition capability on an already constructed Executor, alongside
// (not in place of) EnableAcquisition -- mining dispatch needs its own
// ActionKind/admission/boundary rather than sharing AcquisitionAction's
// single global registration. It reuses the
// AcquisitionBoundary interface shape (InspectAcquisition/Acquire/
// ObserveAcquisition) since the native AcquireResource wire dispatch is
// identical; only the boundary implementation's native read and the action
// accessor it inspects (MineAcquisition, not Acquisition) differ.
func (e *Executor) EnableMineAcquisition(mineAcquisition AcquisitionBoundary) error {
	if mineAcquisition == nil {
		return errors.New("mine acquisition boundary required")
	}
	j, ok := e.journal.(MineAcquisitionJournal)
	if !ok {
		return errors.New("mine acquisition boundary requires typed journal")
	}
	e.mineAcquisition, e.mineAcquisitionJournal = mineAcquisition, j
	return nil
}

func (e *Executor) runMineAcquisition(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	acquisition, ok := action.MineAcquisition()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.mineAcquisition.ObserveAcquisition(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			if !evidence.Complete || evidence.Acquisition != acquisition || !evidence.LaborFinished || !evidence.OutputComplete || !evidence.OutputObserved || evidence.ProducedUnits <= 0 {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			if !evidence.Complete || evidence.Acquisition != acquisition || !evidence.LaborFinished || !evidence.OutputComplete || evidence.ProducedUnits != 0 || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectUnknown, domain.EffectPending:
		default:
			// Missing native attempt/item cannot authorize a second designation.
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
	var inspection AcquisitionInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.mineAcquisition.InspectAcquisition(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Acquisition != acquisition || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.mineAcquisitionJournal.PrepareMineAcquisition(ctx, v.Plan, v.Action, store.MineAcquisitionAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: acquisition.Thing(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.mineAcquisition.Acquire(ctx, AcquisitionDispatch{attempt, inspection.SnapshotToken})
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
