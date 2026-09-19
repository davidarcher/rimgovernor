package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	// Delivered reports a stockpile zone accepting Resource that every
	// covered animal can reach: feed produced on any bench is hauled where
	// they eat it, so the bill need not sit inside their area.
	Delivered bool
	// StorageCells is the connected footprint, shared by every covered
	// animal, on which a Resource-only stockpile zone would make delivery
	// possible when neither a reachable bench nor a delivering zone exists;
	// empty when the covered animals share no free cell.
	StorageCells []domain.Cell
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
	var cells []domain.Cell
	var storage [][]AnimalFeedStorage
	first := true
	for _, t := range targets {
		if !foodID(string(t.ID)) || !validResource(t.Definition) || !foodNumber(t.Nutrition) || len(t.ReachableBenches) > 256 || !validAnimalFeedStorage(t.ReachableStorage, t.StorageCandidates) {
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
		storage = append(storage, t.ReachableStorage)
		if first {
			benches, cells, first = append([]string{}, t.ReachableBenches...), append([]domain.Cell{}, t.StorageCandidates...), false
		} else {
			benches = intersectIDs(benches, t.ReachableBenches)
			cells = intersectCells(cells, t.StorageCandidates)
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
	return AnimalFeedMethod{Reason: AnimalFeedSelected, Resource: bestResource, Target: target, Benches: benches, Delivered: storageDelivers(storage, bestResource), StorageCells: connectedCells(cells)}, nil
}

// validAnimalFeedStorage bounds and checks one animal's reachable storage
// rows and candidate footprint.
func validAnimalFeedStorage(storage []AnimalFeedStorage, candidates []domain.Cell) bool {
	if len(storage) > 256 || len(candidates) > 256 {
		return false
	}
	for _, row := range storage {
		if !foodID(row.Zone) || len(row.Accepts) > 256 {
			return false
		}
		for _, def := range row.Accepts {
			if !validResource(Resource(def)) {
				return false
			}
		}
	}
	for _, cell := range candidates {
		if cell.X < 0 || cell.Z < 0 {
			return false
		}
	}
	return true
}

// storageDelivers reports whether every covered animal (one storage list
// each) can reach a stockpile zone whose filter accepts resource.
func storageDelivers(storage [][]AnimalFeedStorage, resource Resource) bool {
	if len(storage) == 0 {
		return false
	}
	for _, rows := range storage {
		accepted := false
		for _, row := range rows {
			for _, def := range row.Accepts {
				if Resource(def) == resource {
					accepted = true
				}
			}
		}
		if !accepted {
			return false
		}
	}
	return true
}

// intersectCells keeps the cells of a that b also holds, in a's order.
func intersectCells(a, b []domain.Cell) []domain.Cell {
	keep := map[domain.Cell]bool{}
	for _, cell := range b {
		keep[cell] = true
	}
	out := []domain.Cell{}
	for _, cell := range a {
		if keep[cell] {
			out = append(out, cell)
		}
	}
	return out
}

// connectedCells keeps the cardinally connected component of cells that
// holds the first cell (the one nearest the first covered animal), so an
// intersection of several animals' footprints still names one zone.
func connectedCells(cells []domain.Cell) []domain.Cell {
	if len(cells) == 0 {
		return nil
	}
	member := map[domain.Cell]bool{}
	for _, cell := range cells {
		member[cell] = true
	}
	reached := map[domain.Cell]bool{cells[0]: true}
	queue := []domain.Cell{cells[0]}
	for len(queue) > 0 {
		cell := queue[0]
		queue = queue[1:]
		for _, delta := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			next := domain.Cell{X: cell.X + delta.X, Z: cell.Z + delta.Z}
			if member[next] && !reached[next] {
				reached[next] = true
				queue = append(queue, next)
			}
		}
	}
	out := []domain.Cell{}
	for _, cell := range cells {
		if reached[cell] {
			out = append(out, cell)
		}
	}
	return out
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
