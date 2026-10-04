package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineAnimalFeedPlanner proposes MaintainAnimalFeed's resource
// acquisition method: the short race group of policy.ReviewAnimalUpkeep's feed
// reserve review (#1642; the same
// generic animal/food census MaintainHerd and MaintainAnimalContainment
// already read) feeds policy.SelectAnimalFeedMethod's resource/quantity
// selection into the exact
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

func (r *RoutineAnimalFeedPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineResourceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResourceResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineResourceResult{Verdict: BuildingReasonNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.MaintainAnimalFeed, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainAnimalFeed)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !workable {
		return RoutineResourceResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineResourceResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	// A completed bill plan retires at the next review and leaves
	// GoalState.Methods, so the standing-bill check reads the epoch's history.
	history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	standingBill := false
	for _, method := range history {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		standingBill = standingBill || completedBillPlan(plan)
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineResourceResult{}, err
	}
	upkeep := read.Projection.Facts.AnimalUpkeep
	if plan, known := read.Projection.Facts.FoodPlan.Value(); known {
		upkeep.Forecast = domain.Known(plan.Forecast)
	}
	if !foodPlanSupport(read.Projection.Facts.FoodPlan, policy.FoodReserve, "stock-protection") {
		return RoutineResourceResult{Verdict: awaitingFoodPlan("stock-protection")}, nil
	}
	reviewed, err := policy.ReviewAnimalUpkeep(upkeep, review.Latches.Animals, r.reviewer.policy.FoodReserveDays)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	groups, known := reviewed.Feed.Value()
	if !known {
		return RoutineResourceResult{Verdict: fieldUnavailable("animal_feed_reserve")}, nil
	}
	if len(groups) == 0 {
		return RoutineResourceResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	supply, known := upkeep.Food.Value()
	if !known {
		return RoutineResourceResult{Verdict: fieldUnavailable("animal_food_supply")}, nil
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineResourceResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	stock := resourceStockFacts(observed)
	rows, _ := stock.Value()
	have := map[policy.Resource]int64{}
	for _, row := range rows {
		have[row.Resource] = row.Count
	}
	snap.NoteAnimalFeed(call, snap.AnimalFeedCall{Group: groups[0], Stocks: supply.Stocks, Have: have, Races: upkeep.AnimalRaces})
	choice, err := policy.SelectAnimalFeedMethod(groups[0], supply.Stocks, have, upkeep.AnimalRaces)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	switch choice.Reason {
	case policy.AnimalFeedSelected:
	case policy.AnimalFeedNoDeficit:
		return RoutineResourceResult{Verdict: BuildingReasonNoDeficit}, nil
	case policy.AnimalFeedNoFeed:
		return RoutineResourceResult{Verdict: awaitingPlan("animal_feed", "no_source")}, nil
	case policy.AnimalFeedExceedsBound:
		return RoutineResourceResult{Verdict: awaitingPlan("animal_feed", "requirement_exceeds_planning_limit")}, nil
	default:
		return RoutineResourceResult{}, fmt.Errorf("%w: step: animal feed reason %q", ErrControl, choice.Reason)
	}
	// Feed is only feed where the animal can eat it: the bill lands on a
	// bench inside every covered animal's reachable area (#237) or, with no
	// such bench, on any bench once a stockpile accepting the feed sits
	// inside their area for haulers to deliver into -- a zone this planner
	// makes first on the animals' shared free footprint when none exists
	// (#311). With neither bench, zone nor footprint the production path
	// is refused for a window rather than piling feed up out of reach.
	benches := choice.Benches
	if len(benches) == 0 {
		switch {
		case choice.Delivered:
			benches = nil
		case len(choice.StorageCells) > 0:
			clockSchedulerLog("%s: no reachable bench for %s; zoning %d feed storage cells inside the animals' area", goal.Goal.ID, choice.Resource, len(choice.StorageCells))
			result, err := r.core.admitStorageZone(call, epoch, state, goal, review.Tick, choice.Resource, choice.StorageCells, started, "feed-storage")
			if err == nil && (result.Verdict.Is(RefusalSharedAdmission) || result.Verdict.Is(RefusalNoSpace)) {
				// The footprint native offered was refused at preview (the
				// roof or the ground changed): lend the same window the
				// no-bench refusal does rather than parking on no_work.
				result.NativeWorkTicks = stockWaitTicks
			}
			return result, err
		default:
			benches = []string{}
		}
	}
	var ingredients []string
	if choice.Produced {
		if combined, ok := read.Projection.CombinedFoodSupply.Value(); ok {
			ingredients = policy.HumanCookingIngredients(combined, nil, read.Projection.Facts.FoodPlan, policy.HumanMeatFeed)
		}
	}
	result, err := r.core.dispatchResourceGoal(call, epoch, state, goal, review.Tick, identity, choice.Resource, choice.Target, stock, benches, started, ingredients...)
	if err != nil {
		return result, err
	}
	// A completed kibble bill is a standing "do until" bill: its first
	// iteration is what completed the action, the rest needs colonists to
	// keep cooking. With the deficit still open and no new method, ask for
	// game time instead of leaving the clock refused as no_work.
	if result.undispatched() && standingBill {
		result.NativeWorkTicks = animalFeedBillWorkTicks
	}
	return result, nil
}

// animalFeedBillWorkTicks bounds one clock window spent letting a standing
// kibble bill run; the next review re-measures the pet's reachable feed.
const animalFeedBillWorkTicks = domain.TicksPerHour

// completedBillPlan reports a plan whose every action is a production bill
// that reached completed: native placed the bill (or found it standing).
func completedBillPlan(plan store.PlanState) bool {
	if len(plan.Progress) == 0 {
		return false
	}
	for _, progress := range plan.Progress {
		if progress.Action().Kind() != domain.ProductionBillAction || progress.View().Stage != domain.Completed {
			return false
		}
	}
	return true
}
