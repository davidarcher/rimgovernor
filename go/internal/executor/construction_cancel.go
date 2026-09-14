package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ConstructionCancelJournal interface {
	Journal
	PrepareConstructionCancel(context.Context, domain.PlanID, domain.ActionID, store.ConstructionCancelAdmission) (domain.Progress, error)
}

// ConstructionCancelInspection reports the pending construction order that
// currently occupies this placement's cell.
//
// Present false is the one inspection outcome that is neither an error nor a
// reason to dispatch: the exact order is gone, so there is nothing left to
// cancel and nothing native to do. Unlike a zone edit, whose target entity
// keeps its identity, a construction order's native thing changes when a
// blueprint becomes a frame and disappears entirely once built, so identity is
// resolved here rather than carried from proposal time.
type ConstructionCancelInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Cancel                domain.ConstructionCancel
	Present               bool
	ThingID               string
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type ConstructionCancelDispatch struct {
	Attempt       Placement
	ThingID       string
	SnapshotToken string
}
type ConstructionCancelEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Cancel                domain.ConstructionCancel
	Matches               domain.Fact[bool]
}
type ConstructionCancelBoundary interface {
	InspectConstructionCancel(context.Context, Target) (ConstructionCancelInspection, error)
	ApplyConstructionCancel(context.Context, ConstructionCancelDispatch) (Receipt, error)
	ObserveConstructionCancel(context.Context, Placement, domain.GenerationSnapshot) (ConstructionCancelEvidence, error)
}

// EnableConstructionCancel activates the construction-cancel capability; see
// EnableAcquisition for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableConstructionCancel(cancel ConstructionCancelBoundary) error {
	if cancel == nil {
		return errors.New("construction cancel boundary required")
	}
	j, ok := e.journal.(ConstructionCancelJournal)
	if !ok {
		return errors.New("construction cancel boundary requires typed journal")
	}
	e.constructionCancel, e.constructionCancelJournal = cancel, j
	return nil
}

func (e *Executor) runConstructionCancel(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	cancel, ok := action.ConstructionCancel()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.constructionCancel.ObserveConstructionCancel(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			removed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Cancel != cancel || !known || !removed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			removed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Cancel != cancel || !known || removed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
				return result, ErrEvidence
			}
		case domain.EffectUnknown, domain.EffectPending:
		default:
			// Missing native attempt cannot authorize a second cancellation.
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
	var inspection ConstructionCancelInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.constructionCancel.InspectConstructionCancel(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Cancel != cancel || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		// The exact pending order is gone: it was built, removed, or already
		// cancelled. Nothing native can or should happen, and dispatching
		// against a stale identity could hit whatever replaced it, so this
		// terminates the action rather than failing it -- the executor's form
		// of the observed-absent outcome the store reports to players.
		if !inspection.Present {
			next, err := e.journal.Cancel(ctx, v.Plan, v.Action)
			if err != nil {
				return result, err
			}
			result.Progress = next
			return result, nil
		}
		if emergency := policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick); !emergency.Clear {
			result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, emergency, inspection.Tick, result.Progress)
			return result, ErrHeld
		}
		next, err := e.constructionCancelJournal.PrepareConstructionCancel(ctx, v.Plan, v.Action, store.ConstructionCancelAdmission{
			Snapshot: expected, Tick: inspection.Tick, ThingID: inspection.ThingID, SnapshotToken: inspection.SnapshotToken,
			Def: cancel.Definition(), Stuff: cancel.Material(),
		})
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
	receipt, err := e.constructionCancel.ApplyConstructionCancel(ctx, ConstructionCancelDispatch{attempt, inspection.ThingID, inspection.SnapshotToken})
	kind := receipt.Kind
	if err != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != action.ID() || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}
