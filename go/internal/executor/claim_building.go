package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ClaimBuildingJournal is the building-patch admission journal: the claim
// (#459) records the same exact-thing/CAS-token admission a temperature,
// bed medical or claim building patch does.
type ClaimBuildingJournal interface {
	Journal
	PrepareBuildingTemperature(context.Context, domain.PlanID, domain.ActionID, store.BuildingTemperatureAdmission) (domain.Progress, error)
}
type ClaimBuildingInspection struct {
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
	StartedAt, ObservedAt time.Time
	Claim                 domain.ClaimBuilding
	SnapshotToken         string
	Accepted              bool
	Emergency             policy.EmergencySnapshot
}
type ClaimBuildingDispatch struct {
	Attempt       Placement
	SnapshotToken string
}
type ClaimBuildingEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Claim                 domain.ClaimBuilding
	Matches               domain.Fact[bool]
}
type ClaimBuildingBoundary interface {
	InspectClaimBuilding(context.Context, Target) (ClaimBuildingInspection, error)
	ApplyClaimBuilding(context.Context, ClaimBuildingDispatch) (Receipt, error)
	ObserveClaimBuilding(context.Context, Placement, domain.GenerationSnapshot) (ClaimBuildingEvidence, error)
}

// EnableClaimBuilding activates the claim capability, the one-shot CAS
// claim of a claimable building for the player; it shares the
// building-patch admission record with EnableBuildingTemperature.
func (e *Executor) EnableClaimBuilding(claim ClaimBuildingBoundary) error {
	if claim == nil {
		return errors.New("claim building boundary required")
	}
	j, ok := e.journal.(ClaimBuildingJournal)
	if !ok {
		return errors.New("claim building boundary requires typed journal")
	}
	e.claimBuilding, e.claimBuildingJournal = claim, j
	return nil
}

func (e *Executor) runClaimBuilding(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	v := p.View()
	claim, ok := action.ClaimBuilding()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.claimBuilding.ObserveClaimBuilding(ctx, Placement{action, v.Attempt, v.Snapshot, v.Tick}, current)
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
			if !evidence.Complete || evidence.Claim != claim || !known || !allowed {
				return result, ErrEvidence
			}
		case domain.EffectUnsuccessful:
			allowed, known := evidence.Matches.Value()
			if !evidence.Complete || evidence.Claim != claim || !known || allowed || o.UnsuccessfulReason != domain.OutcomeNotAchieved {
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
	var inspection ClaimBuildingInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.claimBuilding.InspectClaimBuilding(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Current != expected || inspection.Claim != claim || !inspection.Accepted || !e.fresh(inspection.StartedAt, inspection.ObservedAt) || !policy.EvaluateEmergency(inspection.Emergency, expected, inspection.Tick).Clear {
			return result, ErrHeld
		}
		next, err := e.claimBuildingJournal.PrepareBuildingTemperature(ctx, v.Plan, v.Action, store.BuildingTemperatureAdmission{Snapshot: expected, Tick: inspection.Tick, Thing: claim.Thing(), SnapshotToken: inspection.SnapshotToken})
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
	receipt, err := e.claimBuilding.ApplyClaimBuilding(ctx, ClaimBuildingDispatch{attempt, inspection.SnapshotToken})
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
