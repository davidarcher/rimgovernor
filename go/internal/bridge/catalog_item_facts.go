package bridge

import (
	"maps"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Stat names the item facts read from the catalog's stat table and
// def rows.
const (
	statMarketValue    = "MarketValue"
	statNutrition      = "Nutrition"
	statMedicalPotency = "MedicalPotency"
	statBeauty         = "Beauty"
)

// ItemFacts are the planner-facing item numbers of the catalog: the
// game's market value and nutrition of every item def, the potency of every
// medicine, the stuff factors, the currency and the drugs. Categories carry
// every thing category a def sits within, parents included. A catalog without a stat table gives the
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
			within, err := catalog.categoriesWithin(name, def)
			if err != nil {
				return policy.ItemFacts{}, err
			}
			items.Categories[resource] = slices.Sorted(maps.Keys(within))
		}
		if len(def.StuffCategories) > 0 {
			items.AcceptedStuff[resource] = def.StuffCategories
		}
		if def.DeepCommonality > 0 {
			items.DeepResources = append(items.DeepResources, resource)
		}
		if a := def.GetApparel(); a != nil && ApparelIsArmor(a) {
			items.Armor = append(items.Armor, resource)
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
	slices.Sort(items.Armor)
	slices.Sort(items.DeepResources)
	items.Currency = policy.Resource(catalog.Constants.CurrencyDef)
	if catalog.ThingDefs[catalog.Constants.CurrencyDef] == nil {
		return policy.ItemFacts{}, contract("catalog has no def row for the currency %s", catalog.Constants.CurrencyDef)
	}
	items.Wort = policy.Resource(catalog.Constants.WortDef)
	if err := buildDrugFacts(catalog, &items); err != nil {
		return policy.ItemFacts{}, err
	}
	var err error
	if items.Sculptures, err = catalog.sculptures(); err != nil {
		return policy.ItemFacts{}, err
	}
	return items, nil
}

// ApparelIsArmor is the apparel policy's armor rule on a def's apparel
// properties: only the game's Soldier outfit tag names it, not Worker too
// (the Soldier outfit wears it, the Worker outfit does not). The rule reads
// ApparelProperties.defaultOutfitTags, the tags the game's own Worker and
// Soldier outfit filters select apparel by (RimWorld.OutfitDatabase
// GenerateStartingOutfits, decompiled with ilspycmd).
//
// Checked against every non-abstract apparel ThingDef of Core, Royalty,
// Ideology, Biotech, Anomaly and Odyssey (Data/*/Defs, ParentName chains
// resolved, Inherit="False" honoured; 112 defs, 31 armor). Soldier without
// Worker holds exactly the flak, plate, recon, power, marine, cataphract and
// locust armors and their helmets, the war mask and veil, the kid helmet, and
// the Royalty psyfocus gear and eltex skullcap/gunlink. Parka, tuque, shirts,
// pants and tribal wear carry Soldier and Worker, so they are clothing;
// untagged and Worker-only defs (packs, belts, hats, robes, the mechanitor
// headsets whose tags reset empty) are clothing as well. No def needed a
// different rule.
func ApparelIsArmor(a *d.ApparelProperties) bool {
	tags := a.GetDefaultOutfitTags()
	return slices.Contains(tags, "Soldier") && !slices.Contains(tags, "Worker")
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
