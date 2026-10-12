package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// StationRecipeFlows are the item flows of every recipe a station works: the
// ingredient slots as alternatives and the products with their counts, as the
// station graph reads them. A recipe whose ingredient slots the catalog cannot
// resolve has none (its flow is unknown, not empty).
func (catalog *DefinitionCatalog) StationRecipeFlows(stations policy.StationCatalog) (map[string]policy.RecipeFlow, error) {
	out := map[string]policy.RecipeFlow{}
	b := stationBuilder{catalog: catalog}
	for _, def := range stations.Defs() {
		for _, r := range stations.Stations[def].Capacity.Recipes {
			if _, done := out[r.Recipe]; done {
				continue
			}
			row, err := catalog.Recipe(r.Recipe)
			if err != nil {
				return nil, err
			}
			flow := policy.RecipeFlow{}
			slots, err := catalog.RecipeIngredients(r.Recipe)
			if err != nil {
				return nil, err
			}
			if v, known := slots.Value(); known {
				flow.Ingredients = v
			}
			for _, p := range row.GetProducts() {
				if product := p.GetValue().GetThingDef(); product != "" && p.GetValue().GetCount() > 0 {
					flow.Products = append(flow.Products, policy.Amount{Resource: policy.Resource(product), Count: int64(p.GetValue().GetCount())})
				}
			}
			slices.SortFunc(flow.Products, func(a, c policy.Amount) int { return compareStrings(string(a.Resource), string(c.Resource)) })
			if flow.Food, err = b.makesHumanFood(row); err != nil {
				return nil, err
			}
			out[r.Recipe] = flow
		}
	}
	return out, nil
}

// StationItemFacts are the item rows the station graph reads of a stock: each
// stocked item's largest stack and whether it rots, and whether each item the
// recipes take in or put out rots. An item the catalog has no row for is an
// error, never a zero.
func (catalog *DefinitionCatalog) StationItemFacts(stock map[policy.Resource]int64, recipes map[string]policy.RecipeFlow) (stackLimit map[policy.Resource]int64, perishable map[policy.Resource]bool, err error) {
	stackLimit, perishable = map[policy.Resource]int64{}, map[policy.Resource]bool{}
	rots := func(r policy.Resource) error {
		if _, done := perishable[r]; done {
			return nil
		}
		_, rot, err := catalog.RotDays(string(r))
		perishable[r] = rot
		return err
	}
	for r, n := range stock {
		if n <= 0 {
			continue
		}
		if stackLimit[r], err = catalog.StackLimit(string(r)); err != nil {
			return nil, nil, err
		}
		if err = rots(r); err != nil {
			return nil, nil, err
		}
	}
	for _, flow := range recipes {
		for _, slot := range flow.Ingredients {
			for _, a := range slot {
				if err = rots(a.Resource); err != nil {
					return nil, nil, err
				}
			}
		}
		for _, a := range flow.Products {
			if err = rots(a.Resource); err != nil {
				return nil, nil, err
			}
		}
	}
	return stackLimit, perishable, nil
}
