package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Workstation stockpiles (#1775): one generic bench-input rule. A bench that
// works a standing bill consumes stored inputs (stone chunks at the
// stonecutter, ingredients at every other bench), so a small stockpile
// holding exactly those inputs stands in the bench's room, nearest the bench
// by walking distance, at a priority above the general store so hauling
// brings the inputs to the bench.

// BenchInput is one bench consuming stored inputs: where it stands and the
// sorted thing definitions its active bills' recipes accept.
type BenchInput struct {
	Bench  string
	Cell   domain.Cell
	Inputs []string
}

// DeriveBenchInputs lists, in bench order, every bench with an active bill
// and its inputs: the union of every ingredient alternative of the recipes
// those bills run. at holds each bench's cell; excluded names the benches
// whose inputs another planner stores (the kitchen's, the butcher's). A
// bench whose bills, recipes or ingredients are unobserved, that stands at
// no known cell, or that has no input to store is not listed: it is planned
// on a pass that knows.
func DeriveBenchInputs(benches []GearBench, at map[string]domain.Cell, excluded map[string]bool) []BenchInput {
	sorted := append([]GearBench(nil), benches...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	var out []BenchInput
	for _, bench := range sorted {
		cell, standing := at[bench.ID]
		bills, billsKnown := bench.Bills.Value()
		recipes, recipesKnown := bench.Recipes.Value()
		if excluded[bench.ID] || !standing || !billsKnown || !recipesKnown {
			continue
		}
		active := map[string]bool{}
		for _, bill := range bills {
			if on, known := bill.Active.Value(); known && on {
				active[bill.Recipe] = true
			}
		}
		seen := map[string]bool{}
		known := true
		var inputs []string
		for _, recipe := range recipes {
			if !active[recipe.Definition] {
				continue
			}
			alternatives, ok := recipe.Ingredients.Value()
			if !ok {
				known = false
				break
			}
			for _, alternative := range alternatives {
				for _, amount := range alternative {
					if name := string(amount.Resource); name != "" && !seen[name] {
						seen[name] = true
						inputs = append(inputs, name)
					}
				}
			}
		}
		if !known || len(inputs) == 0 {
			continue
		}
		sort.Strings(inputs)
		out = append(out, BenchInput{Bench: bench.ID, Cell: cell, Inputs: inputs})
	}
	return out
}

// benchInputSites is one keyed site per bench input: an allow-list
// stockpile at Important priority on a free roofed 2x2 patch inside the room
// holding the bench, nearest the bench by walking distance (#723). A bench
// standing in no census room has no site.
func (r StorageRequest) benchInputSites() []StockpileSite {
	if r.Rooms == nil {
		return nil
	}
	var out []StockpileSite
	for _, input := range r.BenchInputs {
		room, ok := roomHolding(r.Rooms.Rooms, input.Cell)
		if !ok {
			continue
		}
		filter, err := domain.AllowOnlyFilter(input.Inputs)
		if err != nil {
			continue
		}
		candidates, err := r.benchStorageSites(room, input.Cell)
		if err != nil {
			continue
		}
		out = append(out, StockpileSite{Role: domain.IngredientsPrefix + input.Bench, Keyed: true, Room: room.Cells, Filter: filter, Priority: domain.ImportantPriority, Candidates: candidates})
	}
	return out
}

func roomHolding(rooms []Room, cell domain.Cell) (Room, bool) {
	for _, room := range rooms {
		for _, c := range room.Cells {
			if c == cell {
				return room, true
			}
		}
	}
	return Room{}, false
}

// benchStorageSites is roomStorageSites ranked by walking distance to the
// bench instead of straight-line distance.
func (r StorageRequest) benchStorageSites(room Room, bench domain.Cell) ([][]domain.Cell, error) {
	inside := make(map[domain.Cell]bool, len(room.Cells))
	for _, c := range room.Cells {
		inside[c] = true
	}
	var scoped []SiteCell
	for _, c := range r.Cells {
		if inside[c.Cell] {
			scoped = append(scoped, c)
		}
	}
	if len(scoped) == 0 {
		return nil, nil
	}
	sites, err := CoveredStorageSites(CoveredStorageRequest{Bounds: r.Bounds, Anchor: bench, Cells: scoped, Protected: r.Protected})
	if err != nil || len(sites) == 0 {
		return nil, err
	}
	costs, err := HaulCosts(r.Cells, []HaulConsumer{{Cells: []domain.Cell{bench}, Weight: 1}})
	if err != nil {
		return nil, err
	}
	sites = RankSitesByHaul(sites, costs)
	out := make([][]domain.Cell, 0, len(sites))
	for _, site := range sites {
		var block []domain.Cell
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				block = append(block, domain.Cell{X: x, Z: z})
			}
		}
		out = append(out, block)
	}
	return out, nil
}
