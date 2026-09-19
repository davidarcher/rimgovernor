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

type DeconstructionJournal interface {
	Journal
	PrepareDeconstruction(context.Context, domain.PlanID, domain.ActionID, store.DeconstructionAdmission) (domain.Progress, error)
}

var ErrDeconstructionAbsent = errors.New("deconstruction target absent")

type DeconstructionInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Target                domain.Deconstruction
	Eligible              bool
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type DeconstructionDispatch struct {
	Attempt  Placement
	Eligible bool
}
type DeconstructionEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Target                domain.Deconstruction
	Demolished            domain.Fact[bool]
}
type DeconstructionBoundary interface {
	InspectDeconstruction(context.Context, Target) (DeconstructionInspection, error)
	DesignateDeconstruction(context.Context, DeconstructionDispatch) (Receipt, error)
	ObserveDeconstruction(context.Context, Placement, domain.GenerationSnapshot) (DeconstructionEvidence, error)
}

func (e *Executor) EnableDeconstruction(deconstruction DeconstructionBoundary) error {
	if deconstruction == nil {
		return errors.New("deconstruction boundary required")
	}
	j, ok := e.journal.(DeconstructionJournal)
	if !ok {
		return errors.New("deconstruction boundary requires typed journal")
	}
	e.deconstruction, e.deconstructionJournal = deconstruction, j
	return nil
}

func (e *Executor) runDeconstruction(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	target, ok := action.Deconstruction()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.deconstruction.ObserveDeconstruction(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			demolished, known := evidence.Demolished.Value()
			if !evidence.Complete || evidence.Target != target || !known || !demolished {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			demolished, known := evidence.Demolished.Value()
			if !evidence.Complete || evidence.Target != target || !known || demolished || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectAbsent:
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
	var inspection DeconstructionInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.deconstruction.InspectDeconstruction(ctx, Target{action, expected})
		if errors.Is(err, ErrDeconstructionAbsent) {
			next, err := e.journal.Cancel(ctx, v.Plan, v.Action)
			if err != nil {
				return result, err
			}
			result.Progress = next
			return result, fmt.Errorf("%w: deconstruction target absent, action cancelled", ErrHeld)
		}
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Target != target || !inspection.Accepted || !inspection.Eligible || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.deconstructionJournal.PrepareDeconstruction(ctx, v.Plan, v.Action, store.DeconstructionAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: target.Target(), Eligible: inspection.Eligible})
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
	receipt, err := e.deconstruction.DesignateDeconstruction(ctx, DeconstructionDispatch{attempt, inspection.Eligible})
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
