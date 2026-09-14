package policy

import (
	"errors"
	"math"
)

// AnimalFeedReason names MaintainAnimalFeed's resource-selection outcome,
// ported from husbandry.py's update_feed_goal.
type AnimalFeedReason string

const (
	AnimalFeedNoDeficit    AnimalFeedReason = "no_animal_feed_deficit"
	AnimalFeedNoStock      AnimalFeedReason = "no_edible_shared_feed_stock"
	AnimalFeedRestricted   AnimalFeedReason = "feed_resource_restricted_by_player_policy"
	AnimalFeedExceedsBound AnimalFeedReason = "feed_requirement_exceeds_bounded_stock_planning_limit"
	AnimalFeedSelected     AnimalFeedReason = "feed_resource_selected"
)

// AnimalFeedMethod is MaintainAnimalFeed's resource + absolute stock-floor
// selection: the same (resource, target) shape SelectResourceTarget produces
// for MaintainResource, fundable through the identical
// SelectResourceMethod/SelectResourceSources acquisition primitives -- see
// docs/BACKLOG.md 05.6.
type AnimalFeedMethod struct {
	Reason   AnimalFeedReason
	Resource Resource
	Target   int64
}

const maxAnimalFeedTarget = 10000

// SelectAnimalFeedMethod ports husbandry.py's update_feed_goal: among the
// worst-affected deficit race's animals (AnimalFeedTarget is already sorted
// worst-runway-first by ReviewAnimalUpkeep), pick the shared (unheld),
// edible feed stock every one of them can eat, lowest (defName, id) first,
// and require enough stock to cover their combined missing nutrition.
// Unlike Python's per-herd goal ownership bookkeeping, this recomputes fresh
// every tick from the current deficit and stock census -- idempotent,
// content-addressed dispatch like every other RoutineXPlanner. A resource
// under player spending restriction (StoppedResources) is skipped, mirroring
// production_policy.py's resource_policy spending gate. Disclosed narrowing:
// Python selects across the whole herd's future demand; this only covers the
// animals presently below threshold, since Go's AnimalFeedTarget census
// carries only deficit rows.
func SelectAnimalFeedMethod(targets []AnimalFeedTarget, stocks []FoodStock, have map[Resource]int64, stopped []Resource) (AnimalFeedMethod, error) {
	if len(targets) > 256 || len(stocks) > 4096 || len(have) > 4096 {
		return AnimalFeedMethod{}, errors.New("animal feed inputs exceed bound")
	}
	if len(targets) == 0 {
		return AnimalFeedMethod{Reason: AnimalFeedNoDeficit}, nil
	}
	restricted := map[Resource]bool{}
	for _, r := range stopped {
		if !validResource(r) {
			return AnimalFeedMethod{}, errors.New("invalid stopped resource")
		}
		restricted[r] = true
	}
	race := targets[0].Definition
	group := map[PawnID]bool{}
	var missing float64
	for _, t := range targets {
		if !foodID(string(t.ID)) || !validResource(t.Definition) || !foodNumber(t.Nutrition) {
			return AnimalFeedMethod{}, errors.New("invalid animal feed target")
		}
		if t.Definition != race {
			continue
		}
		group[t.ID] = true
		missing += t.Nutrition
	}
	if !foodNumber(missing) || missing <= 0 {
		return AnimalFeedMethod{Reason: AnimalFeedNoDeficit}, nil
	}
	var bestResource Resource
	var bestID string
	var bestNutritionPerItem float64
	found := false
	for _, s := range stocks {
		holder, hk := s.Holder.Value()
		if !hk || holder != "" || s.DefName == "" || !validResource(s.DefName) {
			continue
		}
		count, ck := s.Count.Value()
		nutrition, nk := s.Nutrition.Value()
		if !ck || count <= 0 || !nk || !foodNumber(nutrition) || nutrition <= 0 {
			continue
		}
		eaters := map[PawnID]bool{}
		for _, e := range s.Eaters {
			eaters[e] = true
		}
		covers := true
		for id := range group {
			if !eaters[id] {
				covers = false
				break
			}
		}
		if !covers {
			continue
		}
		if !found || s.DefName < bestResource || (s.DefName == bestResource && s.ID < bestID) {
			bestResource, bestID, bestNutritionPerItem, found = s.DefName, s.ID, nutrition/float64(count), true
		}
	}
	if !found {
		return AnimalFeedMethod{Reason: AnimalFeedNoStock}, nil
	}
	if restricted[bestResource] {
		return AnimalFeedMethod{Reason: AnimalFeedRestricted}, nil
	}
	items := math.Ceil(missing / bestNutritionPerItem)
	if !foodNumber(items) {
		return AnimalFeedMethod{}, errors.New("invalid animal feed item count")
	}
	target := have[bestResource] + int64(items)
	if target <= 0 || target > maxAnimalFeedTarget {
		return AnimalFeedMethod{Reason: AnimalFeedExceedsBound}, nil
	}
	return AnimalFeedMethod{Reason: AnimalFeedSelected, Resource: bestResource, Target: target}, nil
}
