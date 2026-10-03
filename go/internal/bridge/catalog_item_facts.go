package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Stat names the item facts read from the catalog's stat table (#1759) and
// def rows (#1730).
const (
	statMarketValue    = "MarketValue"
	statNutrition      = "Nutrition"
	statMedicalPotency = "MedicalPotency"
	statBeauty         = "Beauty"
)

// ItemFacts are the planner-facing item numbers of the catalog (#1734): the
// game's market value and nutrition of every item def, the potency of every
// medicine, and the stuff factors. A catalog without a stat table gives the
// zero value, on which every lookup fails; a stuff with no market value is
// an error, since it could not be priced.
func (catalog *DefinitionCatalog) ItemFacts() (policy.ItemFacts, error) {
	if catalog == nil {
		return policy.ItemFacts{}, nil
	}
	catalog.itemsOnce.Do(func() { catalog.items, catalog.itemsErr = buildItemFacts(catalog) })
	return catalog.items, catalog.itemsErr
}

func buildItemFacts(catalog *DefinitionCatalog) (policy.ItemFacts, error) {
	if catalog.statValues == nil {
		return policy.ItemFacts{}, nil
	}
	items := policy.ItemFacts{Market: map[policy.Resource]float64{}, Nutrition: map[policy.Resource]float64{}, MedicalPotency: map[policy.Resource]float64{},
		StuffBeauty: map[policy.Resource]float64{}, StuffCategories: map[policy.Resource][]string{}, AcceptedStuff: map[policy.Resource][]string{}, Categories: map[policy.Resource][]string{}}
	for name, def := range catalog.ThingDefs {
		resource := policy.Resource(name)
		if row, ok := catalog.statValues.things[defStuff{name, ""}]; ok {
			if v, shown := row.values[statMarketValue]; shown {
				items.Market[resource] = float64(v)
			}
			if v, shown := row.values[statNutrition]; shown {
				items.Nutrition[resource] = float64(v)
			}
		}
		if len(def.ThingCategories) > 0 {
			items.Categories[resource] = def.ThingCategories
		}
		if len(def.StuffCategories) > 0 {
			items.AcceptedStuff[resource] = def.StuffCategories
		}
		// A medicine is a def whose base stats carry a medical potency
		// (ThingDef.IsMedicine).
		for _, mod := range def.StatBases {
			stat := mod.GetValue()
			if stat.GetStat() == statMedicalPotency {
				items.MedicalPotency[resource] = float64(stat.GetValue())
			}
		}
		if props := def.StuffProps; props != nil {
			items.StuffCategories[resource] = props.Categories
			items.StuffBeauty[resource] = stuffFactor(props, statBeauty)
			if _, priced := items.Market[resource]; !priced {
				return policy.ItemFacts{}, contract("catalog stuff %s has no market value", name)
			}
		}
	}
	return items, nil
}

// FixtureItemFacts fixes the catalog's item facts for a test catalog that
// stands in for a loaded one; a decoded catalog derives them itself.
func (catalog *DefinitionCatalog) FixtureItemFacts(items policy.ItemFacts) *DefinitionCatalog {
	catalog.itemsOnce.Do(func() { catalog.items = items })
	return catalog
}

// stuffFactor is the stuff's stat factor, 1 when it has none.
func stuffFactor(props *d.StuffProperties, stat string) float64 {
	for _, mod := range props.StatFactors {
		factor := mod.GetValue()
		if factor.GetStat() == stat {
			return float64(factor.GetValue())
		}
	}
	return 1
}
