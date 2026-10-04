package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePermitsPlanner is MaintainPermits' planner (#1606, epic #1598):
// while a colonist holds permit points for a permit worth taking
// (policy.NextPermit over the royalty read), it commits one
// choose_permit pawn setting for that colonist, faction and permit. The committed
// plan is the persisted PermitIntent: it lives on the goal's method in the
// save. A permit native refuses is not retried past the attempt limit, so the
// next best waits behind it only after the goal epoch turns.
type RoutinePermitsPlanner struct {
	reviewer *RoutineReviewer
}
type RoutinePermitsResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutinePermitsPlanner(reviewer *RoutineReviewer) (*RoutinePermitsPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainPermits) {
		return nil, fmt.Errorf("%w: NewRoutinePermitsPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainPermits)", ErrControl)
	}
	return &RoutinePermitsPlanner{reviewer}, nil
}

func (r *RoutinePermitsPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePermitsResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePermitsResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePermitsResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePermitsResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePermitsResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPermits)
	if err != nil {
		return RoutinePermitsResult{}, err
	}
	if !workable {
		return RoutinePermitsResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePermitsResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePermitsResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePermitsResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePermitsResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePermitsResult{}, err
	}
	choice, ok := policy.NextPermitOf(read.Projection.Facts.Royalty)
	if !ok {
		return RoutinePermitsResult{Verdict: waitFor(WaitMethodUsed, "permit")}, nil
	}
	intent := choice.Intent()
	method, plan, exhausted, err := permitMethod(intent, goal.History, goal.Goal.Epoch)
	if err != nil {
		return RoutinePermitsResult{}, err
	}
	if exhausted {
		return RoutinePermitsResult{Verdict: refuse(RefusalRetriesSpent, "permit_attempts", "")}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(intent.Holder)}) {
		return RoutinePermitsResult{Verdict: waitFor(WaitMethodUsed, "pawn_claim")}, nil
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePermitsResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePermitsResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, fmt.Sprintf("permits: %s takes %s with %s", intent.Holder, intent.Permit, intent.Faction), plan); err != nil {
		return RoutinePermitsResult{}, err
	}
	return RoutinePermitsResult{Verdict: BuildingReasonAdmitted, Plan: plan.ID()}, nil
}

// permitMethod builds the one-write plan that records a permit intent and the
// method key that names it: keyed by holder, permit and attempt count, like
// the other one-write planners, so a retry after an interrupted try
// re-selects the current best. exhausted reports the attempt limit spent.
func permitMethod(intent policy.PermitIntent, history []domain.GoalMethod, epoch uint64) (method domain.MethodID, plan domain.PlanSpec, exhausted bool, err error) {
	prefix := fmt.Sprintf("permit-%s-%s-", intent.Holder, intent.Permit)
	attempt := medicalAttemptCount(history, epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return "", domain.PlanSpec{}, true, nil
	}
	setting, err := intent.Setting()
	if err != nil {
		return "", domain.PlanSpec{}, false, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewPawnSettingsAction(domain.ActionID(fmt.Sprintf("%s-0", id)), setting)
	if err != nil {
		return "", domain.PlanSpec{}, false, err
	}
	plan, err = domain.NewPlan(id, 1, []domain.Action{action})
	return domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt)), plan, false, err
}
