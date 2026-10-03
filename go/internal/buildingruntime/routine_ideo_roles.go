package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineIdeoRolesPlanner is MaintainIdeoRoles' planner (#1661, epic #1653):
// while an active role has a free place and a fitting believer
// (policy.RoleAssignments over the ideology section and the pawn rows), it
// commits one Assign of the role precept to that believer, with no previous
// assignment (a pawn that holds a role is never moved). The committed plan is
// the persisted intent on the goal's method. An assignment native refuses is
// tried at most maxMedicalAttemptsPerPatient times per pawn and role per goal
// epoch.
type RoutineIdeoRolesPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineIdeoRolesResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineIdeoRolesPlanner(reviewer *RoutineReviewer) (*RoutineIdeoRolesPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainIdeoRoles) {
		return nil, fmt.Errorf("%w: NewRoutineIdeoRolesPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainIdeoRoles)", ErrControl)
	}
	return &RoutineIdeoRolesPlanner{reviewer}, nil
}

func (r *RoutineIdeoRolesPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineIdeoRolesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineIdeoRolesResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineIdeoRolesResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineIdeoRolesResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineIdeoRolesResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainIdeoRoles)
	if err != nil {
		return RoutineIdeoRolesResult{}, err
	}
	if !workable {
		return RoutineIdeoRolesResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineIdeoRolesResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineIdeoRolesResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineIdeoRolesResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineIdeoRolesResult{}, err
	}
	owed, known := policy.RoleAssignments(read.Projection.Facts.Ideology, read.Projection.WorkPawns).Value()
	if !known || len(owed) == 0 {
		return RoutineIdeoRolesResult{Verdict: BuildingReasonUsed}, nil
	}
	// The first assignment whose attempts are not spent; one in flight per
	// step keeps a role with several places filling one believer at a time.
	for _, choice := range owed {
		prefix := fmt.Sprintf("ideorole-%s-%s-", choice.Pawn, choice.Role)
		attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			continue
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			return RoutineIdeoRolesResult{Verdict: BuildingReasonUsed}, nil
		}
		assign, err := domain.NewAssign(domain.PawnID(choice.Pawn), choice.Role, domain.ClearPrevious())
		if err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewAssignAction(domain.ActionID(fmt.Sprintf("%s-0", id)), assign)
		if err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoutineIdeoRolesResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, fmt.Sprintf("ideology: %s takes role %s (%s)", choice.Pawn, choice.Def, choice.Role), plan); err != nil {
			return RoutineIdeoRolesResult{}, err
		}
		return RoutineIdeoRolesResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
	}
	return RoutineIdeoRolesResult{Verdict: BuildingReasonExhausted}, nil
}
