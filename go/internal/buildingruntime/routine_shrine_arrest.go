package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func shrineArrestPlan(id domain.PlanID, performer, target domain.PawnID, bed string) (domain.PlanSpec, error) {
	draft, err := domain.NewOwnedDraft(performer)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	draftAction, err := domain.NewOwnedDraftAction(domain.ActionID(string(id)+"-draft"), draft)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	intent, err := domain.NewArrest(performer, target, bed)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	arrest, err := domain.NewCaptureAction(domain.ActionID(string(id)+"-arrest"), intent)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	return domain.NewPlan(id, 1, []domain.Action{draftAction, arrest}, domain.ActionDependency{Action: arrest.ID(), Requires: draftAction.ID(), Coupled: true})
}

func (r *RoutinePopulationCustodyPlanner) commitArrest(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.StandardState, facts policy.RoutineFacts, squad []policy.ShrineDefenderFacts, target domain.PawnID, arbiter *stepArbiter) (RoutinePopulationCustodyResult, error) {
	bed := policy.ShrineArrestBed(facts.Sleeping)
	performer := policy.ShrineArrester(squad)
	if bed == "" || performer == "" || performer == target {
		return RoutinePopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "shrine_arrest")}, nil
	}
	prefix := fmt.Sprintf("population-arrest-%s-", target)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	plan, err := shrineArrestPlan(id, performer, target, bed)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !arbiter.tryClaim([]domain.PawnID{performer, target}, "bed:"+bed) {
		return RoutinePopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "arrest_claim")}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: commitArrest: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	return RoutinePopulationCustodyResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
