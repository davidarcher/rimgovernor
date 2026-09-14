package executor

import (
	"context"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func caravanDepartureAdmission(snapshot domain.GenerationSnapshot, tick domain.Tick, facts policy.CaravanDepartureFacts) store.CaravanDepartureAdmission {
	crew := make([]store.CaravanCrewAdmission, len(facts.Crew))
	for i, member := range facts.Crew {
		crew[i] = store.CaravanCrewAdmission{Pawn: member.Pawn, SnapshotToken: member.SnapshotToken}
	}
	return store.CaravanDepartureAdmission{Snapshot: snapshot, Tick: tick, Crew: crew, CatalogToken: facts.CatalogToken}
}

func (e *Executor) runCaravanDeparture(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	if _, ok := action.CaravanDeparture(); !ok || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
	if v.Unresolved {
		return e.reconcileCaravanDeparture(ctx, result, generation)
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
	minimum := v.Tick
	var inspection CaravanDepartureInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.caravanDeparture.InspectCaravanDeparture(ctx, Target{action, expected})
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
		minimum = max(minimum, facts.PawnTick)
		decision := policy.EvaluateCaravanDeparture(policy.CaravanDepartureRequest{Action: action, Progress: result.Progress, Current: expected, MinimumTick: minimum, Policy: inspection.Policy, Facts: facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, minimum, result.Progress)
			return result, ErrHeld
		}
		admission := caravanDepartureAdmission(expected, facts.PreviewTick, facts)
		next, err := e.caravanDepartureJournal.PrepareCaravanDeparture(ctx, v.Plan, v.Action, admission)
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
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Facts.PreviewTick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Facts.PreviewTick}
	admission := caravanDepartureAdmission(expected, inspection.Facts.PreviewTick, inspection.Facts)
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.caravanDeparture.DepartCaravan(ctx, CaravanDepartureDispatch{attempt, admission})
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

func (e *Executor) reconcileCaravanDeparture(ctx context.Context, result Result, generation context.Context) (Result, error) {
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
	var admission store.CaravanDepartureAdmission
	found := false
	for _, record := range state.CaravanDepartureAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	evidence, err := e.caravanDeparture.ObserveCaravanDeparture(ctx, CaravanDepartureDispatch{Placement{p.Action(), v.Attempt, v.Snapshot, v.Tick}, admission}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	departure, _ := p.Action().CaravanDeparture()
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || !slices.Equal(evidence.Crew, departure.Crew()) || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
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
	// A completed FormCaravan is the only point this vertical treats a
	// crew as "away": ObserveCaravanDeparture records that completion and
	// begins world-progression tracking in one commit, so no crash window
	// can leave a completed departure with no tracking record for
	// CaravanJourneyTracker to eventually reconcile.
	next, err := e.caravanDepartureJournal.ObserveCaravanDeparture(ctx, v.Plan, o, current, evidence.CaravanID, evidence.Crew)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
