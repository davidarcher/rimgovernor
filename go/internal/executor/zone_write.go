package executor

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ZoneWriteJournal is the building-patch admission journal: every zone
// write (a zone deletion #611, a zone cell edit, a stockpile patch) records
// the same exact-thing/CAS-token admission a temperature, bed medical or
// claim building patch does, the thing being the zone or storage target.
type ZoneWriteJournal interface {
	Journal
	PrepareBuildingTemperature(context.Context, domain.PlanID, domain.ActionID, store.BuildingTemperatureAdmission) (domain.Progress, error)
}

// ZoneWriteInspection, Dispatch and Evidence carry any zone write kind
// (zone_delete, zone_cell_edit, stockpile_patch) through one one-shot CAS
// settings-write path; Action is the exact action inspected or observed.
type ZoneWriteInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Action                domain.Action
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type ZoneWriteDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type ZoneWriteEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Action                domain.Action
	Matches               domain.Fact[bool]
}
type ZoneWriteBoundary interface {
	InspectZoneWrite(context.Context, Target) (ZoneWriteInspection, error)
	ApplyZoneWrite(context.Context, ZoneWriteDispatch) (Receipt, error)
	ObserveZoneWrite(context.Context, Placement, domain.GenerationSnapshot) (ZoneWriteEvidence, error)
}

// zoneWriteTarget is the admitted thing and CAS before-token a zone write
// names.
func zoneWriteTarget(action domain.Action) (thing, token string, ok bool) {
	if d, ok := action.ZoneDelete(); ok {
		return d.Zone(), d.BeforeToken(), true
	}
	if e, ok := action.ZoneCellEdit(); ok {
		return e.Zone(), e.BeforeToken(), true
	}
	if p, ok := action.StockpilePatch(); ok {
		return p.Target(), p.BeforeToken(), true
	}
	return "", "", false
}

// EnableZoneWrite activates a zone write boundary for the given kinds
// (zone_delete, zone_cell_edit, stockpile_patch); every kind shares the
// building-patch admission record with EnableBuildingTemperature.
func (e *Executor) EnableZoneWrite(w ZoneWriteBoundary, kinds ...domain.ActionKind) error {
	if w == nil {
		return errors.New("zone write boundary required")
	}
	j, ok := e.journal.(ZoneWriteJournal)
	if !ok {
		return errors.New("zone write boundary requires typed journal")
	}
	for _, kind := range kinds {
		switch kind {
		case domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction:
		default:
			return fmt.Errorf("zone write boundary cannot run %s", kind)
		}
	}
	if e.zoneWrite == nil {
		e.zoneWrite = map[domain.ActionKind]ZoneWriteBoundary{}
	}
	for _, kind := range kinds {
		e.zoneWrite[kind] = w
	}
	e.zoneWriteJournal = j
	return nil
}

func (e *Executor) runZoneWrite(ctx context.Context, w ZoneWriteBoundary, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	thing, token, ok := zoneWriteTarget(action)
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := w.ObserveZoneWrite(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
	var inspection ZoneWriteInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = w.InspectZoneWrite(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Action != action || inspection.SnapshotToken != token || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) || !policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick).Clear {
			return result, ErrHeld
		}
		next, err := e.zoneWriteJournal.PrepareBuildingTemperature(ctx, v.Plan, v.Action, store.BuildingTemperatureAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: thing, SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := w.ApplyZoneWrite(ctx, ZoneWriteDispatch{attempt, inspection.SnapshotToken})
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
