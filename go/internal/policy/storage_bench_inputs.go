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
