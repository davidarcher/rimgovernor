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

type SupplyJournal interface {
	Journal
	PrepareSupply(context.Context, domain.PlanID, domain.ActionID, store.SupplyAdmission) (domain.Progress, error)
}

// ErrSupplyAbsent reports a fresh cell read that no longer lists the exact
// supply an Allow targets: it was eaten, hauled aside by a builder, merged
// or allowed by the player. The proposal can never succeed, so the executor
// cancels it instead of holding the plan (#114).
var ErrSupplyAbsent = errors.New("supply target absent")

type SupplyInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Supply                domain.SupplyAllow
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type SupplyDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type SupplyEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Supply                domain.SupplyAllow
	Allowed               domain.Fact[bool]
}
type SupplyBoundary interface {
	InspectSupply(context.Context, Target) (SupplyInspection, error)
	AllowSupply(context.Context, SupplyDispatch) (Receipt, error)
	ObserveSupply(context.Context, Placement, domain.GenerationSnapshot) (SupplyEvidence, error)
}

// EnableSupply activates the supply capability; see EnableAcquisition for
// why capabilities are wired this way instead of inferred from a composed
// Boundary.
func (e *Executor) EnableSupply(supply SupplyBoundary) error {
	if supply == nil {
		return errors.New("supply boundary required")
	}
	j, ok := e.journal.(SupplyJournal)
	if !ok {
		return errors.New("supply boundary requires typed journal")
	}
	e.supply, e.supplyJournal = supply, j
	return nil
}

func (e *Executor) runSupply(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	supply, ok := action.SupplyAllow()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.supply.ObserveSupply(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			allowed, known := evidence.Allowed.Value()
			if !evidence.Complete || evidence.Supply != supply || !known || !allowed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			allowed, known := evidence.Allowed.Value()
			if !evidence.Complete || evidence.Supply != supply || !known || allowed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectUnknown, domain.EffectPending:
		default:
			// Missing native attempt/item cannot authorize a second Allow.
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
	var inspection SupplyInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.supply.InspectSupply(ctx, Target{action, expected})
		if errors.Is(err, ErrSupplyAbsent) {
			// Settle the proposal so the plan closes and the planner
			// re-proposes from the next census, as haul does for a thing
			// that left its cell; holding it kept every remaining starting
			// supply forbidden (#114).
			next, err := e.journal.Cancel(ctx, v.Plan, v.Action)
			if err != nil {
				return result, err
			}
			result.Progress = next
			return result, fmt.Errorf("%w: supply target absent, action cancelled", ErrHeld)
		}
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Supply != supply || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.supplyJournal.PrepareSupply(ctx, v.Plan, v.Action, store.SupplyAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: supply.Thing(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.supply.AllowSupply(ctx, SupplyDispatch{attempt, inspection.SnapshotToken})
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
