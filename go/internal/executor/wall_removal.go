package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (e *Executor) runWallRemoval(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	removal, ok := action.WallRemoval()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
	if v.Unresolved {
		return e.reconcileWallRemoval(ctx, result, generation)
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
	var inspection WallRemovalInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.wallRemoval.InspectWallRemoval(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Facts.Snapshot != expected || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		facts := inspection.Facts
		backupIdentity := ""
		if removal.BackupOf() != "" {
			state, err := e.journal.LoadPlan(ctx, v.Plan)
			if err != nil {
				return result, err
			}
			found := false
			for i, candidate := range state.Spec.Actions() {
				if candidate.ID() != removal.BackupOf() {
					continue
				}
				found = true
				view := state.Progress[i].View()
				identity, known := view.Construction.Value()
				effect, ek := view.Effect.Value()
				if view.Stage == domain.Completed && known && ek && effect == domain.EffectCompleted {
					backupIdentity = identity.Current
				}
				break
			}
			if !found {
				return result, ErrEvidence
			}
		}
		decision := policy.EvaluateWallRemoval(policy.WallRemovalRequest{Action: action, Progress: result.Progress, Current: expected, BackupIdentity: backupIdentity, Facts: facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, facts.ObservationTick, result.Progress)
			return result, ErrHeld
		}
		identity, _ := facts.TargetIdentity.Value()
		eligible, _ := facts.SiteEligible.Value()
		admission := store.WallRemovalAdmission{Snapshot: expected, Tick: facts.ObservationTick, Original: removal.Original(), BackupOf: removal.BackupOf(), TargetIdentity: identity, SiteEligible: eligible}
		next, err := e.wallRemovalJournal.PrepareWallRemoval(ctx, v.Plan, v.Action, admission)
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
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Facts.ObservationTick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Facts.ObservationTick}
	identity, _ := inspection.Facts.TargetIdentity.Value()
	eligible, _ := inspection.Facts.SiteEligible.Value()
	admission := store.WallRemovalAdmission{Snapshot: expected, Tick: inspection.Facts.ObservationTick, Original: removal.Original(), BackupOf: removal.BackupOf(), TargetIdentity: identity, SiteEligible: eligible}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.wallRemoval.ExecuteWallRemoval(ctx, WallRemovalDispatch{attempt, admission})
	kind := receipt.Kind
	if err != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}

func (e *Executor) reconcileWallRemoval(ctx context.Context, result Result, generation context.Context) (Result, error) {
	p := result.Progress
	v := p.View()
	current := e.current().Snapshot
	if current.Validate() != nil || current.Native == 0 || current.Native < v.Snapshot.Native || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
		return result, ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, v.Plan)
	if err != nil {
		return result, err
	}
	var admission store.WallRemovalAdmission
	found := false
	for _, record := range state.WallRemovalAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	evidence, err := e.wallRemoval.ObserveWallRemoval(ctx, WallRemovalDispatch{Placement{p.Action(), v.Attempt, v.Snapshot, v.Tick}, admission}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || evidence.Target != admission.TargetIdentity || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrEvidence
	}
	switch o.Effect {
	case domain.EffectCompleted, domain.EffectAbsent, domain.EffectUnsuccessful:
		if !evidence.Complete || o.Causality != domain.AfterDispatch {
			return result, ErrEvidence
		}
	case domain.EffectUnknown, domain.EffectPending:
	default:
		return result, ErrEvidence
	}
	next, err := e.journal.Observe(ctx, v.Plan, o, current)
	if err != nil {
		return result, err
	}
	result.Progress = next
	if evidence.Retired {
		if cascadeErr := e.cascadeWallRemovalRetirement(ctx, state, v.Action); cascadeErr != nil {
			return result, cascadeErr
		}
	}
	return result, nil
}

// cascadeWallRemovalRetirement mirrors wall_upgrade.py's retire_batch: native
// invalidated a pending demolition or backup removal, so same-plan actions
// that transitively require it can no longer proceed as planned and are
// cancelled rather than left to admit against a step that will never
// complete. This is vertical-specific by design, not a generic Store
// primitive, since only this bundle's dependency shape ever needs it.
func (e *Executor) cascadeWallRemovalRetirement(ctx context.Context, state store.PlanState, invalidated domain.ActionID) error {
	dependents := map[domain.ActionID]bool{invalidated: true}
	for changed := true; changed; {
		changed = false
		for _, dep := range state.Spec.Dependencies() {
			if dependents[dep.Requires] && !dependents[dep.Action] {
				dependents[dep.Action] = true
				changed = true
			}
		}
	}
	delete(dependents, invalidated)
	for id := range dependents {
		for i, candidate := range state.Spec.Actions() {
			if candidate.ID() != id {
				continue
			}
			switch state.Progress[i].View().Stage {
			case domain.Pending, domain.Prepared:
				if _, err := e.journal.Cancel(ctx, state.Spec.ID(), id); err != nil {
					return err
				}
			}
			break
		}
	}
	return nil
}
