package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsIdeoRolesPlanner is MaintainIdeoRoles' planner:
// while an active role has a free place and a fitting believer
// (policy.RoleAssignments over the ideology section and the pawn rows), it
// commits one Assign of the role precept to that believer, with no previous
// assignment (a pawn that holds a role is never moved). The committed plan is
// the persisted intent on the goal's method. An assignment native refuses is
// tried again as the shared refusal budget allows for its pawn and role.
type RoundsIdeoRolesPlanner struct {
	reviewer *Rounder
}
type RoundsIdeoRolesResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsIdeoRolesPlanner(reviewer *Rounder) (*RoundsIdeoRolesPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainIdeoRoles) {
		return nil, fmt.Errorf("%w: NewRoundsIdeoRolesPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainIdeoRoles)", ErrControl)
	}
	return &RoundsIdeoRolesPlanner{reviewer}, nil
}

func (r *RoundsIdeoRolesPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsIdeoRolesResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsIdeoRolesResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsIdeoRolesResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsIdeoRolesResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsIdeoRolesResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainIdeoRoles)
	if err != nil {
		return RoundsIdeoRolesResult{}, err
	}
	if !workable {
		return RoundsIdeoRolesResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsIdeoRolesResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsIdeoRolesResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsIdeoRolesResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsIdeoRolesResult{}, err
	}
	owed, known := policy.RoleAssignments(read.Projection.Facts.Ideology, read.Projection.WorkPawns).Value()
	if !known || len(owed) == 0 {
		return RoundsIdeoRolesResult{Verdict: waitFor(policy.CauseMethodUsed, "ideology_roles")}, nil
	}
	// The first assignment the refusal budget admits; one in flight per
	// step keeps a role with several places filling one believer at a time.
	// owed is non-empty, so a loop that admits none ends on the last hold.
	var held Verdict
	for _, choice := range owed {
		prefix := fmt.Sprintf("ideorole-%s-%s-", choice.Pawn, choice.Role)
		method, verdict, ok, err := admitStandardMethod(call, p.journal, goal, prefix, state.Snapshot)
		if err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		if !ok {
			held = verdict
			continue
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Pawn)}) {
			return RoundsIdeoRolesResult{Verdict: waitFor(policy.CauseMethodUsed, "pawn_claim")}, nil
		}
		assign, err := domain.NewAssign(domain.PawnID(choice.Pawn), choice.Role, domain.ClearPrevious())
		if err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewAssignAction(domain.ActionID(fmt.Sprintf("%s-0", id)), assign)
		if err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsIdeoRolesResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, fmt.Sprintf("ideology: %s takes role %s (%s)", choice.Pawn, choice.Def, choice.Role), plan); err != nil {
			return RoundsIdeoRolesResult{}, err
		}
		return RoundsIdeoRolesResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
	}
	return RoundsIdeoRolesResult{Verdict: held}, nil
}
