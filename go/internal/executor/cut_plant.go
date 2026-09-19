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

// The cut-plant family (#245) is the supply Allow's shape on one exact
// blighted plant: inspect (census + preview + emergency), record the CAS
// admission, dispatch the CutPlant designation, observe the plant until it
// is gone (completed), still designated (pending) or undesignated by the
// player (unsuccessful).
type CutPlantJournal interface {
	Journal
	PrepareCutPlant(context.Context, domain.PlanID, domain.ActionID, store.CutPlantAdmission) (domain.Progress, error)
}

// ErrCutPlantAbsent reports a fresh census that no longer lists the exact
// plant undesignated at its cell: it was cut, died, or the player designated
// it. The proposal can never succeed, so the executor cancels it.
var ErrCutPlantAbsent = errors.New("cut plant target absent")

type CutPlantInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Plant                 domain.CutPlant
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type CutPlantDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type CutPlantEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Plant                 domain.CutPlant
	Designated            domain.Fact[bool]
}
type CutPlantBoundary interface {
	InspectCutPlant(context.Context, Target) (CutPlantInspection, error)
	DesignateCutPlant(context.Context, CutPlantDispatch) (Receipt, error)
	ObserveCutPlant(context.Context, Placement, domain.GenerationSnapshot) (CutPlantEvidence, error)
}

// EnableCutPlant activates the cut-plant capability; see EnableAcquisition
// for why capabilities are wired this way instead of inferred from a
// composed Boundary.
func (e *Executor) EnableCutPlant(cutPlant CutPlantBoundary) error {
	if cutPlant == nil {
		return errors.New("cut plant boundary required")
	}
	j, ok := e.journal.(CutPlantJournal)
	if !ok {
		return errors.New("cut plant boundary requires typed journal")
	}
	e.cutPlant, e.cutPlantJournal = cutPlant, j
	return nil
}

func (e *Executor) runCutPlant(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	plant, ok := action.CutPlant()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.cutPlant.ObserveCutPlant(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			designated, known := evidence.Designated.Value()
			if !evidence.Complete || evidence.Plant != plant || !known || !designated {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			designated, known := evidence.Designated.Value()
			if !evidence.Complete || evidence.Plant != plant || !known || designated || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectAbsent:
			// The native ledger has no entry for the attempt: the dispatch
			// timed out before admission (#71), so the designation never
			// ran and the action returns to Pending for a fresh attempt
			// under a fresh token. Only the boundary's complete
			// post-dispatch lookup says so.
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
	var inspection CutPlantInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.cutPlant.InspectCutPlant(ctx, Target{action, expected})
		if errors.Is(err, ErrCutPlantAbsent) {
			// Settle the proposal so the plan closes and the planner
			// re-proposes from the next census, as supply does for a stack
			// that left its cell.
			next, err := e.journal.Cancel(ctx, v.Plan, v.Action)
			if err != nil {
				return result, err
			}
			result.Progress = next
			return result, fmt.Errorf("%w: cut plant target absent, action cancelled", ErrHeld)
		}
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Plant != plant || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.cutPlantJournal.PrepareCutPlant(ctx, v.Plan, v.Action, store.CutPlantAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: plant.Plant(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.cutPlant.DesignateCutPlant(ctx, CutPlantDispatch{attempt, inspection.SnapshotToken})
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
