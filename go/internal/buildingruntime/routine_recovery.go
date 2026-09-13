package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineRecoveryPlanner proposes one RecoveryService action for
// RecoverDisasterServices' service-job half (repair, breakdown restoration,
// refuel): policy.SelectRecoveryMethods already ranks and validates
// candidates, ordering exposure protection (RecoveryAreaProposal) strictly
// before repair/breakdown/refuel work (RecoveryServiceProposal). Only the
// latter is dispatchable today; a RecoveryAreaProposal winner (no native
// restriction-admission operation exists yet) is left open, the same way
// RoutineGearPlanner leaves the workshop-bill half of MaintainEquipment inert.
//
// Unlike RoutineGearPlanner, this planner re-derives its selection with a
// fresh full colony census (observation.ObserveRoutineOwned against the
// reviewer's own RoutineSource, not a narrower per-family source) because
// RecoveryWorkers requires the mood-pawn census only that full pipeline
// produces; see routine_recovery.go in package store for the analogous
// review-cycle computation this mirrors at dispatch time.
type RoutineRecoveryPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineRecoveryResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineRecoveryPlanner(reviewer *RoutineReviewer) (*RoutineRecoveryPlanner, error) {
	if reviewer == nil {
		return nil, ErrControl
	}
	return &RoutineRecoveryPlanner{reviewer}, nil
}
func (r *RoutineRecoveryPlanner) Step(ctx context.Context) (RoutineRecoveryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func recoveryServiceMethod(method policy.RecoveryMethod) (domain.RecoveryMethod, bool) {
	switch method {
	case policy.RecoveryRepair:
		return domain.RecoveryServiceRepair, true
	case policy.RecoveryBreakdown:
		return domain.RecoveryServiceBreakdown, true
	case policy.RecoveryRefuel:
		return domain.RecoveryServiceRefuel, true
	default:
		return "", false
	}
}

func (r *RoutineRecoveryPlanner) step(call, epoch context.Context) (RoutineRecoveryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRecoveryResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineRecoveryResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineRecoveryResult{Reason: BuildingMethodNoReview}, nil
	}
	if review.Disaster == nil {
		return RoutineRecoveryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.RecoverDisasterServices {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineRecoveryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineRecoveryResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineRecoveryResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineRecoveryResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	facts := read.Projection.Facts
	planning := policy.RecoveryPlanning{Safety: facts.RecoverySafety, Workers: facts.RecoveryWorkers, Buildings: facts.RecoveryBuildings}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	selection, err := policy.SelectRecoveryMethods(planning, review.Disaster, seen, expected.Tick)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if err = selection.Validate(); err != nil {
		return RoutineRecoveryResult{}, err
	}
	var chosen *policy.RecoveryCandidate
	for i := range selection.Candidates {
		if selection.Candidates[i].Kind == policy.RecoveryServiceProposal {
			chosen = &selection.Candidates[i]
			break
		}
	}
	if chosen == nil {
		return RoutineRecoveryResult{Reason: BuildingMethodUsed}, nil
	}
	method, ok := recoveryServiceMethod(chosen.Method)
	if !ok {
		return RoutineRecoveryResult{}, ErrControl
	}
	service, err := domain.NewRecoveryService(domain.PawnID(chosen.Pawn), chosen.Building, method)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, chosen.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-recovery-%x", digest[:16]))
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineRecoveryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineRecoveryResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, chosen.ID, plan); err != nil {
		return RoutineRecoveryResult{}, err
	}
	return RoutineRecoveryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
