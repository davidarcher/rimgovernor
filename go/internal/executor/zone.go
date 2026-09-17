package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

type ZoneJournal interface {
	Journal
	PrepareZone(context.Context, domain.PlanID, domain.ActionID, store.ZoneAdmission) (domain.Progress, error)
}
type ZoneInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Zone                  domain.ZoneCreate
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type ZoneDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type ZoneEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Zone                  domain.ZoneCreate
	Matches               domain.Fact[bool]
}
type ZoneBoundary interface {
	InspectZone(context.Context, Target) (ZoneInspection, error)
	CreateZone(context.Context, ZoneDispatch) (Receipt, error)
	ObserveZone(context.Context, Placement, domain.GenerationSnapshot) (ZoneEvidence, error)
}

// EnableZone activates the zone capability; see EnableAcquisition for why
// capabilities are wired this way instead of inferred from a composed
// Boundary.
func (e *Executor) EnableZone(zone ZoneBoundary) error {
	if zone == nil {
		return errors.New("zone boundary required")
	}
	j, ok := e.journal.(ZoneJournal)
	if !ok {
		return errors.New("zone boundary requires typed journal")
	}
	e.zone, e.zoneJournal = zone, j
	return nil
}

func (e *Executor) runZone(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	zone, ok := action.ZoneCreate()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.zone.ObserveZone(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			if !evidence.Complete || evidence.Zone != zone || !known || !allowed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			allowed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Zone != zone || !known || allowed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectUnknown, domain.EffectPending:
		default:
			// Missing native attempt or zone cannot authorize a second creation.
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
	var inspection ZoneInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.zone.InspectZone(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Zone != zone || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.zoneJournal.PrepareZone(ctx, v.Plan, v.Action, store.ZoneAdmission{Snapshot: expected, Tick: inspection.Tick, SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.zone.CreateZone(ctx, ZoneDispatch{attempt, inspection.SnapshotToken})
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
