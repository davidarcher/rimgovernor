package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineAnimalFeedPlanner proposes MaintainAnimalFeed's resource
// acquisition method: policy.ReviewAnimalUpkeep's Feed deficit (the same
// generic animal/food census MaintainHerd and MaintainAnimalContainment
// already read) feeds policy.SelectAnimalFeedMethod's resource/quantity
// selection (ported from husbandry.py's update_feed_goal) into the exact
// same bench/recipe production and native mine/harvest acquisition pipeline
// RoutineResourcePlanner already established for MaintainResource -- see
// dispatchResourceGoal in routine_resource.go. This was blocked on 05.5's
// MaintainResource-* acquisition plumbing landing first; see docs/BACKLOG.md
// 05.6.
type RoutineAnimalFeedPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResourceSource
	core     *RoutineResourcePlanner
}

func NewRoutineAnimalFeedPlanner(reviewer *RoutineReviewer, native RoutineResourceSource) (*RoutineAnimalFeedPlanner, error) {
	core, err := NewRoutineResourcePlanner(reviewer, native)
	if err != nil {
		return nil, err
	}
	return &RoutineAnimalFeedPlanner{reviewer, native, core}, nil
}

func (r *RoutineAnimalFeedPlanner) Step(ctx context.Context) (RoutineResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineAnimalFeedPlanner) step(call, epoch context.Context) (RoutineResourceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResourceResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResourceResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineResourceResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainAnimalFeed {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineResourceResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineResourceResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identityReply, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	expected, err := observation.DecodeIdentity(identityReply)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineResourceResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineResourceResult{}, err
	}
	upkeep := read.Projection.Facts.AnimalUpkeep
	reviewed, err := policy.ReviewAnimalUpkeep(upkeep, review.Latches.Animals, r.reviewer.policy.AnimalUpkeep)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	targets, known := reviewed.Feed.Value()
	if !known || len(targets) == 0 {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	}
	supply, known := upkeep.Food.Value()
	if !known {
		return RoutineResourceResult{Reason: BuildingMethodUnknown}, nil
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineResourceResult{}, ErrControl
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineResourceResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineResourceResult{}, ErrControl
	}
	stock := resourceStockFacts(observed)
	rows, _ := stock.Value()
	have := map[policy.Resource]int64{}
	for _, row := range rows {
		have[row.Resource] = row.Count
	}
	choice, err := policy.SelectAnimalFeedMethod(targets, supply.Stocks, have, r.reviewer.policy.StoppedResources)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if choice.Reason != policy.AnimalFeedSelected {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	}
	return r.core.dispatchResourceGoal(call, epoch, state, goal, review.Tick, identity, choice.Resource, choice.Target, stock, started)
}
