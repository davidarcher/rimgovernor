package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// StockpileWriteInspection, Dispatch and Evidence carry either stockpile
// write kind (zone_cell_edit, stockpile_patch) through the same one-shot
// CAS settings-write path a zone deletion takes; Action is the exact
// action inspected or observed.
type StockpileWriteInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Action                domain.Action
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type StockpileWriteDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type StockpileWriteEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Action                domain.Action
	Matches               domain.Fact[bool]
}
type StockpileWriteBoundary interface {
	InspectStockpileWrite(context.Context, Target) (StockpileWriteInspection, error)
	ApplyStockpileWrite(context.Context, StockpileWriteDispatch) (Receipt, error)
	ObserveStockpileWrite(context.Context, Placement, domain.GenerationSnapshot) (StockpileWriteEvidence, error)
}

func stockpileWriteTarget(action domain.Action) (thing, token string, ok bool) {
	if e, ok := action.ZoneCellEdit(); ok {
		return e.Zone(), e.BeforeToken(), true
	}
	if p, ok := action.StockpilePatch(); ok {
		return p.Target(), p.BeforeToken(), true
	}
	return "", "", false
}

// EnableStockpileWrite activates the zone cell edit and stockpile patch
// capability; it shares the building-patch admission record with
// EnableZoneDelete.
func (e *Executor) EnableStockpileWrite(w StockpileWriteBoundary) error {
	if w == nil {
		return errors.New("stockpile write boundary required")
	}
	j, ok := e.journal.(ZoneDeleteJournal)
	if !ok {
		return errors.New("stockpile write boundary requires typed journal")
	}
	e.stockpileWrite, e.stockpileWriteJournal = w, j
	return nil
}

func (e *Executor) runStockpileWrite(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	thing, token, ok := stockpileWriteTarget(action)
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.stockpileWrite.ObserveStockpileWrite(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			allowed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Action != action || !known || !allowed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			allowed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Action != action || !known || allowed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectAbsent:
			// The native ledger has no entry for the attempt: the dispatch
			// timed out before admission (#71), so the settings write never ran
			// and the action returns to Pending for a fresh attempt (#165).
			// Only the boundary's complete post-dispatch lookup says so;
			// anything less cannot authorize a second settings write.
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
	var inspection StockpileWriteInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.stockpileWrite.InspectStockpileWrite(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Action != action || inspection.SnapshotToken != token || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) || !policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick).Clear {
			return result, ErrHeld
		}
		next, err := e.stockpileWriteJournal.PrepareBuildingTemperature(ctx, v.Plan, v.Action, store.BuildingTemperatureAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: thing, SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.stockpileWrite.ApplyStockpileWrite(ctx, StockpileWriteDispatch{attempt, inspection.SnapshotToken})
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
