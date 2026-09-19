package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

type AcquisitionJournal interface {
	Journal
	PrepareAcquisition(context.Context, domain.PlanID, domain.ActionID, store.AcquisitionAdmission) (domain.Progress, error)
	Withdraw(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
}
type AcquisitionInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Acquisition           domain.Acquisition
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type AcquisitionDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type AcquisitionEvidence struct {
	Observation                                   domain.Observation
	StartedAt, ObservedAt                         time.Time
	Complete                                      bool
	Acquisition                                   domain.Acquisition
	LaborFinished, OutputObserved, OutputComplete bool
	ProducedUnits                                 int32
	// Designated is whether the source still carries the designation the
	// dispatch placed; an unsuccessful effect without labor requires it gone.
	Designated bool
	// PendingReason is native's account of why a still-designated harvest
	// has no worker (#291); empty unless the effect is pending.
	PendingReason string
}
type AcquisitionBoundary interface {
	InspectAcquisition(context.Context, Target) (AcquisitionInspection, error)
	Acquire(context.Context, AcquisitionDispatch) (Receipt, error)
	// WithdrawAcquisition cancels the designation Acquire placed, under the
	// withdrawal attempt the journal opened (#291).
	WithdrawAcquisition(context.Context, AcquisitionDispatch) (Receipt, error)
	ObserveAcquisition(context.Context, Placement, domain.GenerationSnapshot) (AcquisitionEvidence, error)
}

// EnableAcquisition activates the acquisition capability on an already
// constructed Executor. Capabilities are wired this way, one call per
// capability with its own typed boundary value, rather than inferred by
// type-asserting a single composed Boundary: composing several optional
// capabilities into one value so a type assertion can "discover" them is
// indistinguishable, from the assertion's perspective, between a capability
// that was deliberately configured and one merely embedded as a nil
// interface to satisfy an unrelated capability's composition — the latter
// reports present and panics on first use. Explicit Enable* calls make an
// unconfigured capability simply absent instead.
func (e *Executor) EnableAcquisition(acquisition AcquisitionBoundary) error {
	if acquisition == nil {
		return errors.New("acquisition boundary required")
	}
	j, ok := e.journal.(AcquisitionJournal)
	if !ok {
		return errors.New("acquisition boundary requires typed journal")
	}
	e.acquisition, e.acquisitionJournal = acquisition, j
	return nil
}

func (e *Executor) runAcquisition(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	acquisition, ok := action.Acquisition()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.acquisition.ObserveAcquisition(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			// Labor finished with nothing produced, or the designation left
			// the source before any labor (withdrawn or removed; #291).
			settled := evidence.LaborFinished && evidence.OutputComplete || !evidence.LaborFinished && !evidence.Designated
			if !evidence.Complete || evidence.Acquisition != acquisition || !settled || evidence.ProducedUnits != 0 || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
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
		case domain.EffectPending:
			result.Detail = evidence.PendingReason
			if v.Stage == domain.Cancelled && evidence.Designated {
				// The journal cancelled the action but the designation is
				// still on the source: withdraw it
				// natively so the effect can settle (#291).
				next, err := e.journal.Observe(ctx, v.Plan, o, current)
				if err != nil {
					return result, err
				}
				result.Progress = next
				return e.withdrawAcquisition(ctx, action, next, authority, generation, o.Tick)
			}
		case domain.EffectUnknown:
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
	var inspection AcquisitionInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.acquisition.InspectAcquisition(ctx, Target{action, expected})
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
		next, err := e.acquisitionJournal.PrepareAcquisition(ctx, v.Plan, v.Action, store.AcquisitionAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: acquisition.Thing(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.acquisition.Acquire(ctx, AcquisitionDispatch{attempt, inspection.SnapshotToken})
	return e.recordAcquisition(ctx, result, v.Plan, next, attempt, receipt, err)
}

// withdrawAcquisition is the native half of a cancelled acquisition whose
// designation remains: open the withdrawal attempt durably, then
// ask native to remove the designation under the current authority. The
// receipt is journaled like a dispatch's; the next run observes the attempt
// and the record settles as unsuccessful once the designation is gone.
func (e *Executor) withdrawAcquisition(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context, tick domain.Tick) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	next, err := e.acquisitionJournal.Withdraw(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	result.NativeCalled = true
	receipt, err := e.acquisition.WithdrawAcquisition(ctx, AcquisitionDispatch{Attempt: attempt})
	return e.recordAcquisition(ctx, result, v.Plan, next, attempt, receipt, err)
}

func (e *Executor) recordAcquisition(ctx context.Context, result Result, plan domain.PlanID, next domain.Progress, attempt Placement, receipt Receipt, err error) (Result, error) {
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != attempt.Action.ID() || receipt.Attempt != attempt.Attempt || receipt.Snapshot != attempt.Snapshot {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, plan, attempt, kind, errors.Join(err, ctx.Err()))
}
