package policy

import (
	"errors"
	"math"
	"sort"
)

// AnimalFeedReason names MaintainAnimalFeed's resource-selection outcome.
type AnimalFeedReason string

const (
	AnimalFeedNoDeficit    AnimalFeedReason = "no_animal_feed_deficit"
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
	// Benches names the work tables every covered animal can reach inside
	// its allowed area (sorted): the only benches a production bill may
	// land on, since a bill drops its product where it is made and a
	// confined animal cannot walk to a bench elsewhere. Empty when no
	// bench is shared by the covered animals.
	Benches []string
}

const maxAnimalFeedTarget = 10000

// AnimalFeedFallbackResource is produced when no shared stock covers the
// deficit animals; animalFeedFallbackNutrition is RimWorld's per-item
// nutrition for it (Kibble: 0.05).
const (
	AnimalFeedFallbackResource  Resource = "Kibble"
	animalFeedFallbackNutrition          = 0.05
)

// SelectAnimalFeedMethod picks the feed stock for a deficit herd: among the
// worst-affected deficit race's animals (AnimalFeedTarget is already sorted
// worst-runway-first by ReviewAnimalUpkeep), pick the shared (unheld),
// edible feed stock every one of them can eat, lowest (defName, id) first,
// and require enough stock to cover their combined missing nutrition.
// The selection is recomputed fresh every tick from the current deficit and
// stock census -- idempotent, content-addressed dispatch like every other
// RoutineXPlanner. A resource
// under player spending restriction (StoppedResources) is skipped, the same
// gate the resource-policy planner applies. Only the animals presently below
// threshold are covered, since AnimalFeedTarget carries only deficit rows.
// When no shared stock covers them at all, the method is kibble production
// (AnimalFeedFallbackResource) sized by the same missing nutrition.
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
	var benches []string
	first := true
	for _, t := range targets {
		if !foodID(string(t.ID)) || !validResource(t.Definition) || !foodNumber(t.Nutrition) || len(t.ReachableBenches) > 256 {
			return AnimalFeedMethod{}, errors.New("invalid animal feed target")
		}
		for _, bench := range t.ReachableBenches {
			if !foodID(bench) {
				return AnimalFeedMethod{}, errors.New("invalid animal feed target")
			}
		}
		if t.Definition != race {
			continue
		}
		group[t.ID] = true
		missing += t.Nutrition
		if first {
			benches, first = append([]string{}, t.ReachableBenches...), false
		} else {
			benches = intersectIDs(benches, t.ReachableBenches)
		}
	}
	sort.Strings(benches)
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
		// Nothing the animals can reach: fall back to producing kibble, the
		// one feed every animal eats and any butcher spot makes from meat and
		// hay. The bench's output lands where it is made, so a confined
		// animal is fed by a bench inside its area rather than by stock it
		// cannot walk to.
		bestResource, bestNutritionPerItem = AnimalFeedFallbackResource, animalFeedFallbackNutrition
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
	return AnimalFeedMethod{Reason: AnimalFeedSelected, Resource: bestResource, Target: target, Benches: benches}, nil
}

// intersectIDs keeps the entries of a that b also holds, in a's order.
func intersectIDs(a, b []string) []string {
	keep := map[string]bool{}
	for _, id := range b {
		keep[id] = true
	}
	out := []string{}
	for _, id := range a {
		if keep[id] {
			out = append(out, id)
		}
	}
	return out
}
