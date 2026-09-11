package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewWithMelee(journal MeleeJournal, building Boundary, draft DraftBoundary, melee MeleeBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if melee == nil {
		return nil, errors.New("melee boundary required")
	}
	e, err := NewWithDraft(journal, building, draft, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.meleeJournal, e.melee = journal, melee
	return e, nil
}

func meleeProgress(state store.PlanState, id domain.ActionID) (domain.Progress, bool) {
	for _, p := range state.Progress {
		if p.View().Action == id {
			return p, true
		}
	}
	return domain.Progress{}, false
}

func (e *Executor) runMelee(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	m, ok := action.MeleeAttack()
	if !ok || progress.Action() != action {
		return result, ErrEvidence
	}
	v := progress.View()
	if v.Unresolved {
		return e.reconcileMelee(ctx, result, generation)
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
		return result, ErrAuthority
	}
	minimum := v.Tick
	var inspection MeleeInspection
	var admission store.MeleeAdmission
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		state, err := e.journal.LoadPlan(ctx, v.Plan)
		if err != nil {
			return result, err
		}
		latest, exists := meleeProgress(state, v.Action)
		if !exists || latest.Action() != action {
			return result, ErrEvidence
		}
		result.Progress = latest
		prerequisite, exists := meleeProgress(state, m.DraftAction())
		if !exists {
			return result, ErrEvidence
		}
		cleanup, known := prerequisite.View().DraftCleanup.Value()
		claim, claimed := cleanup.Claim.Value()
		if !known || !claimed || prerequisite.View().Stage != domain.Completed || prerequisite.View().Unresolved || cleanup.Stage != domain.DraftCleanupRequired || claim.Action != m.DraftAction() || claim.Pawn != m.Pawn() || claim.Origin != expected || prerequisite.View().Snapshot != expected {
			return result, ErrHeld
		}
		minimum = max(minimum, latest.View().Tick, prerequisite.View().Tick)
		for _, old := range state.MeleeAdmissions {
			if old.Action == v.Action {
				minimum = max(minimum, old.Admission.Tick)
			}
		}
		inspection, err = e.melee.InspectMelee(ctx, Target{action, expected}, claim)
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		decision := policy.EvaluateMeleeDefense(policy.MeleeDefenseRequest{Action: action, Progress: latest, DraftProgress: prerequisite, Current: expected, MinimumTick: minimum, Facts: inspection.Facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			return result, ErrHeld
		}
		facts := inspection.Facts
		admission = store.MeleeAdmission{Snapshot: expected, Tick: facts.PreviewTick, Pawn: m.Pawn(), Target: m.Target(), PawnSnapshotToken: facts.Pawn.SnapshotToken, TargetSnapshotToken: facts.Target.SnapshotToken, DraftClaim: claim}
		next, err := e.meleeJournal.PrepareMelee(ctx, v.Plan, v.Action, admission)
		if err != nil {
			return result, err
		}
		result.Progress = next
		minimum = max(minimum, facts.PreviewTick)
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, admission.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, admission.Tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.melee.AttackMelee(ctx, MeleeDispatch{attempt, admission})
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

func (e *Executor) reconcileMelee(ctx context.Context, result Result, generation context.Context) (Result, error) {
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
	var admission store.MeleeAdmission
	found := false
	for _, record := range state.MeleeAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	evidence, err := e.melee.ObserveMelee(ctx, MeleeDispatch{draftAttempt(p.Action(), p), admission}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	m, _ := p.Action().MeleeAttack()
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || evidence.Pawn != m.Pawn() || evidence.Target != m.Target() || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
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
	// The journal preserves cancelled intent while recording later causal effects.
	next, err := e.journal.Observe(ctx, v.Plan, o, current)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
