package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
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

func (r *RoutinePopulationCustodyPlanner) commitArrest(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.GoalState, facts policy.RoutineFacts, squad []policy.ShrineDefenderFacts, target domain.PawnID, arbiter *stepArbiter) (RoutinePopulationCustodyResult, error) {
	bed := policy.ShrineArrestBed(facts.Sleeping)
	performer := policy.ShrineArrester(squad)
	if bed == "" || performer == "" || performer == target {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
	}
	prefix := fmt.Sprintf("population-arrest-%s-", target)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-population-custody-%x", digest[:16]))
	plan, err := shrineArrestPlan(id, performer, target, bed)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !arbiter.tryClaim([]domain.PawnID{performer, target}, "bed:"+bed) {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	return RoutinePopulationCustodyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
