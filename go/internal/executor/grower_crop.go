package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// GrowerCropJournal is the building-patch admission journal: the grower
// crop patch records the same exact-thing/CAS-token admission a temperature
// or bed medical patch does.
type GrowerCropJournal interface {
	Journal
	PrepareBuildingTemperature(context.Context, domain.PlanID, domain.ActionID, store.BuildingTemperatureAdmission) (domain.Progress, error)
}
type GrowerCropInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Crop                  domain.GrowerCrop
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type GrowerCropDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type GrowerCropEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Crop                  domain.GrowerCrop
	Matches               domain.Fact[bool]
}
type GrowerCropBoundary interface {
	InspectGrowerCrop(context.Context, Target) (GrowerCropInspection, error)
	ApplyGrowerCrop(context.Context, GrowerCropDispatch) (Receipt, error)
	ObserveGrowerCrop(context.Context, Placement, domain.GenerationSnapshot) (GrowerCropEvidence, error)
}

// EnableGrowerCrop activates the grower-crop capability, the one-shot CAS
// patch of the crop a plant grower sows; it shares the building-patch
// admission record with EnableBuildingTemperature. See EnableAcquisition for
// why capabilities are wired this way instead of inferred from a composed
// Boundary.
func (e *Executor) EnableGrowerCrop(crop GrowerCropBoundary) error {
	if crop == nil {
		return errors.New("grower crop boundary required")
	}
	j, ok := e.journal.(GrowerCropJournal)
	if !ok {
		return errors.New("grower crop boundary requires typed journal")
	}
	e.growerCrop, e.growerCropJournal = crop, j
	return nil
}

func (e *Executor) runGrowerCrop(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	crop, ok := action.GrowerCrop()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.growerCrop.ObserveGrowerCrop(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			if !evidence.Complete || evidence.Crop != crop || !known || !allowed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			allowed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Crop != crop || !known || allowed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectUnknown, domain.EffectPending:
		default:
			// Missing native attempt cannot authorize a second settings write.
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
	var inspection GrowerCropInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.growerCrop.InspectGrowerCrop(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Crop != crop || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) || !policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick).Clear {
			return result, ErrHeld
		}
		next, err := e.growerCropJournal.PrepareBuildingTemperature(ctx, v.Plan, v.Action, store.BuildingTemperatureAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: crop.Thing(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.growerCrop.ApplyGrowerCrop(ctx, GrowerCropDispatch{attempt, inspection.SnapshotToken})
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
