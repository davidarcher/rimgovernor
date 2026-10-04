package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AnimalFeedGroup is one race's standing herd feed reserve (#1642): the
// nutrition the group's animals eat in the reserve window against the unheld
// edible stock every one of them can eat. Only groups below target are
// reported.
type AnimalFeedGroup struct {
	Definition Resource
	// Animals are the group's feedable animals (sorted).
	Animals                                           []PawnID
	TargetNutrition, StockNutrition, DeficitNutrition float64
	// ReachableBenches names the work tables every animal of the group can
	// reach inside its allowed area (sorted): the only benches a production
	// bill may land on, since a bill drops its product where it is made and a
	// confined animal cannot walk to a bench elsewhere.
	ReachableBenches []string
	// ReachableStorage is each animal's reachable stockpile zones.
	ReachableStorage [][]AnimalFeedStorage
	// StorageCandidates is the connected footprint, shared by the whole
	// group, on which a feed-only stockpile zone would make delivery possible.
	StorageCandidates []domain.Cell
}

// ReviewAnimalFeedReserve keeps the herd fed like the human food reserve
// (ReviewFoodReserve): per race group the target is reserveDays x the group
// NutritionPerDay, the stock is the unheld edible stock every animal of the
// group can eat, and the deficit is the nutrition left to produce. Stock
// several races can eat counts toward each of them (accepted). The review is
// unknown while the food census or the forecast is.
func ReviewAnimalFeedReserve(v AnimalUpkeepObservation, eligible []UpkeepAnimal, reserveDays float64) (domain.Fact[[]AnimalFeedGroup], error) {
	if !foodNumber(reserveDays) || reserveDays < 0 {
		return domain.Unknown[[]AnimalFeedGroup](), errors.New("invalid animal feed reserve days")
	}
	if len(eligible) == 0 {
		return domain.Known([]AnimalFeedGroup{}), nil
	}
	supply, known := v.Food.Value()
	if !known {
		return domain.Unknown[[]AnimalFeedGroup](), nil
	}
	forecast, reviewed := v.Forecast.Value()
	if !reviewed {
		ids := make([]PawnID, len(eligible))
		for i, animal := range eligible {
			ids[i] = animal.ID
		}
		var err error
		if forecast, err = ForecastFood(supply, ids); err != nil {
			return domain.Unknown[[]AnimalFeedGroup](), nil
		}
	}
	perDay := map[PawnID]float64{}
	for _, row := range forecast.Consumers {
		perDay[row.ID] = row.NutritionPerDay
	}
	byRace := map[Resource]*AnimalFeedGroup{}
	for _, animal := range eligible {
		need, exists := perDay[animal.ID]
		if !exists {
			return domain.Unknown[[]AnimalFeedGroup](), nil
		}
		if !foodNumber(need) {
			return domain.Unknown[[]AnimalFeedGroup](), errors.New("invalid animal nutrition per day")
		}
		g := byRace[animal.Definition]
		if g == nil {
			g = &AnimalFeedGroup{Definition: animal.Definition, ReachableBenches: append([]string{}, animal.ReachableBenches...), StorageCandidates: append([]domain.Cell{}, animal.StorageCandidates...)}
			byRace[animal.Definition] = g
		} else {
			g.ReachableBenches = intersectIDs(g.ReachableBenches, animal.ReachableBenches)
			g.StorageCandidates = intersectCells(g.StorageCandidates, animal.StorageCandidates)
		}
		g.Animals = append(g.Animals, animal.ID)
		g.ReachableStorage = append(g.ReachableStorage, animal.ReachableStorage)
		g.TargetNutrition += reserveDays * need
	}
	groups := []AnimalFeedGroup{}
	for _, g := range byRace {
		sort.Slice(g.Animals, func(i, j int) bool { return g.Animals[i] < g.Animals[j] })
		sort.Strings(g.ReachableBenches)
		for _, stock := range supply.Stocks {
			if !groupEats(stock, g.Animals) {
				continue
			}
			nutrition, _ := stock.Nutrition.Value()
			g.StockNutrition += nutrition
		}
		if !foodNumber(g.TargetNutrition) || !foodNumber(g.StockNutrition) {
			return domain.Unknown[[]AnimalFeedGroup](), errors.New("invalid animal feed reserve nutrition")
		}
		g.DeficitNutrition = max(0, g.TargetNutrition-g.StockNutrition)
		if g.DeficitNutrition > 0 {
			g.StorageCandidates = connectedCells(g.StorageCandidates)
			groups = append(groups, *g)
		}
	}
	sort.Slice(groups, func(i, j int) bool {
		if groups[i].DeficitNutrition != groups[j].DeficitNutrition {
			return groups[i].DeficitNutrition > groups[j].DeficitNutrition
		}
		return groups[i].Definition < groups[j].Definition
	})
	return domain.Known(groups), nil
}

// groupEats reports unheld stock with positive known count and nutrition
// that every one of the animals can eat.
func groupEats(s FoodStock, animals []PawnID) bool {
	holder, hk := s.Holder.Value()
	if !hk || holder != "" || s.DefName == "" || !validResource(s.DefName) {
		return false
	}
	count, ck := s.Count.Value()
	nutrition, nk := s.Nutrition.Value()
	if !ck || count <= 0 || !nk || !foodNumber(nutrition) || nutrition <= 0 {
		return false
	}
	eaters := map[PawnID]bool{}
	for _, e := range s.Eaters {
		eaters[e] = true
	}
	for _, id := range animals {
		if !eaters[id] {
			return false
		}
	}
	return true
}

// AnimalFeedReason names MaintainAnimalFeed's resource-selection outcome.
type AnimalFeedReason string

const (
	AnimalFeedNoDeficit    AnimalFeedReason = "no_animal_feed_deficit"
	AnimalFeedExceedsBound AnimalFeedReason = "feed_requirement_exceeds_bounded_stock_planning_limit"
	AnimalFeedSelected     AnimalFeedReason = "feed_resource_selected"
	// AnimalFeedNoFeed: no stock covers the group and no recipe produces
	// anything its race can eat.
	AnimalFeedNoFeed AnimalFeedReason = "no_feed"
)

// AnimalFeedMethod is MaintainAnimalFeed's resource + absolute stock-floor
// selection: the same (resource, target) shape SelectResourceTarget produces
// for MaintainResource, fundable through the identical
// SelectResourceMethod/SelectResourceSources acquisition primitives.
type AnimalFeedMethod struct {
	Reason   AnimalFeedReason
	Resource Resource
	// Produced reports Resource is made on a bench (no stock covers the group).
	Produced bool
	Target   int64
	// Benches names the work tables every animal of the group can reach
	// (the group ReachableBenches); empty when none is shared.
	Benches []string
	// Delivered reports a stockpile zone accepting Resource that every
	// animal of the group can reach: feed produced on any bench is hauled
	// where they eat it, so the bill need not sit inside their area.
	Delivered bool
	// StorageCells is the connected footprint, shared by the group, on which
	// a Resource-only stockpile zone would make delivery possible when
	// neither a reachable bench nor a delivering zone exists.
	StorageCells []domain.Cell
}

const maxAnimalFeedTarget = 10000

// SelectAnimalFeedMethod picks the feed that tops one short group reserve
// up: the lowest (defName, id) shared stock the whole group can eat, sized
// by the group deficit nutrition. With no such stock the method produces
// the race lowest-nutrition producible feed item (the cheapest grade); with
// none, the reason is AnimalFeedNoFeed.
func SelectAnimalFeedMethod(group AnimalFeedGroup, stocks []FoodStock, have map[Resource]int64, races AnimalRaceCatalog) (AnimalFeedMethod, error) {
	if !validResource(group.Definition) || len(group.Animals) == 0 || !foodNumber(group.DeficitNutrition) {
		return AnimalFeedMethod{}, errors.New("invalid animal feed group")
	}
	if group.DeficitNutrition <= 0 {
		return AnimalFeedMethod{Reason: AnimalFeedNoDeficit}, nil
	}
	var bestResource Resource
	var bestID string
	var bestNutritionPerItem float64
	found := false
	for _, s := range stocks {
		if !groupEats(s, group.Animals) {
			continue
		}
		count, _ := s.Count.Value()
		nutrition, _ := s.Nutrition.Value()
		if !found || s.DefName < bestResource || (s.DefName == bestResource && s.ID < bestID) {
			bestResource, bestID, bestNutritionPerItem, found = s.DefName, s.ID, nutrition/float64(count), true
		}
	}
	produced := false
	if !found {
		// Nothing the animals can reach: produce the cheapest feed item a
		// recipe makes that the race can eat. The bench output lands where
		// it is made, so a confined animal is fed by a bench inside its area
		// rather than by stock it cannot walk to.
		catalog, known := races.Race(group.Definition)
		if !known {
			return AnimalFeedMethod{}, errors.New("animal race missing from catalog")
		}
		for _, item := range catalog.FeedItems {
			if !validResource(item.Def) || !foodNumber(item.Nutrition) || item.Nutrition <= 0 {
				return AnimalFeedMethod{}, errors.New("invalid animal race feed item")
			}
			if !produced || item.Nutrition < bestNutritionPerItem || (item.Nutrition == bestNutritionPerItem && item.Def < bestResource) {
				bestResource, bestNutritionPerItem, produced = item.Def, item.Nutrition, true
			}
		}
		if !produced {
			return AnimalFeedMethod{Reason: AnimalFeedNoFeed}, nil
		}
	}
	items := math.Ceil(group.DeficitNutrition / bestNutritionPerItem)
	if !foodNumber(items) {
		return AnimalFeedMethod{}, errors.New("invalid animal feed item count")
	}
	target := have[bestResource] + int64(items)
	if target <= 0 || target > maxAnimalFeedTarget {
		return AnimalFeedMethod{Reason: AnimalFeedExceedsBound}, nil
	}
	return AnimalFeedMethod{Reason: AnimalFeedSelected, Resource: bestResource, Produced: produced, Target: target, Benches: group.ReachableBenches, Delivered: storageDelivers(group.ReachableStorage, bestResource), StorageCells: group.StorageCandidates}, nil
}

// validAnimalFeedStorage bounds and checks one animal's reachable storage
// rows and candidate footprint.
func validAnimalFeedStorage(storage []AnimalFeedStorage, candidates []domain.Cell) bool {
	for _, row := range storage {
		if !foodID(row.Zone) {
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
