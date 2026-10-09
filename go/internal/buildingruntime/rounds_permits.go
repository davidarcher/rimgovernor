package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsPermitsPlanner is MaintainPermits' planner:
// while a colonist holds permit points for a permit worth taking
// (policy.NextPermit over the royalty read), it commits one
// choose_permit pawn setting for that colonist, faction and permit. The committed
// plan is the persisted PermitIntent: it lives on the goal's method in the
// save. A permit native refuses is not retried past the attempt limit, so the
// next best waits behind it only after the Episode turns.
type RoundsPermitsPlanner struct {
	reviewer *Rounder
}
type RoundsPermitsResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsPermitsPlanner(reviewer *Rounder) (*RoundsPermitsPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainPermits) {
		return nil, fmt.Errorf("%w: NewRoundsPermitsPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainPermits)", ErrControl)
	}
	return &RoundsPermitsPlanner{reviewer}, nil
}

func (r *RoundsPermitsPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsPermitsResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsPermitsResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsPermitsResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsPermitsResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPermits)
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	if !workable {
		return RoundsPermitsResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsPermitsResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsPermitsResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsPermitsResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	choice, ok := policy.NextPermitOf(read.Projection.Facts.Royalty)
	if !ok {
		return RoundsPermitsResult{Verdict: waitFor(WaitMethodUsed, "permit")}, nil
	}
	intent := choice.Intent()
	method, verdict, admitted, err := admitStandardMethod(call, p.journal, goal, permitMethodPrefix(intent), state.Snapshot)
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	if !admitted {
		return RoundsPermitsResult{Verdict: verdict}, nil
	}
	plan, err := permitPlan(intent)
	if err != nil {
		return RoundsPermitsResult{}, err
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(intent.Holder)}) {
		return RoundsPermitsResult{Verdict: waitFor(WaitMethodUsed, "pawn_claim")}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPermitsResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPermitsResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, fmt.Sprintf("permits: %s takes %s with %s", intent.Holder, intent.Permit, intent.Faction), plan); err != nil {
		return RoundsPermitsResult{}, err
	}
	return RoundsPermitsResult{Verdict: BuildingReasonAdmitted, Plan: plan.ID()}, nil
}

// permitMethodPrefix keys a permit method by holder and permit, like the other
// one-write planners, so a retry after an interrupted try re-selects the
// current best.
func permitMethodPrefix(intent policy.PermitIntent) string {
	return fmt.Sprintf("permit-%s-%s-", intent.Holder, intent.Permit)
}

// permitPlan builds the one-write plan that records a permit intent.
func permitPlan(intent policy.PermitIntent) (domain.PlanSpec, error) {
	setting, err := intent.Setting()
	if err != nil {
		return domain.PlanSpec{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewPawnSettingsAction(domain.ActionID(fmt.Sprintf("%s-0", id)), setting)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	return domain.NewPlan(id, 1, []domain.Action{action})
}
