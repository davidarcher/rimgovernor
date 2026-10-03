package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// RecipeFacts are the recipe facts the planners read, derived from the
// RecipeDef rows once per catalog (#1721). A nil catalog has none.
func (catalog *DefinitionCatalog) RecipeFacts() (policy.RecipeFacts, error) {
	if catalog == nil {
		return policy.RecipeFacts{}, nil
	}
	catalog.recipes.factsOnce.Do(func() { catalog.recipes.facts, catalog.recipes.factsErr = catalog.recipeFacts() })
	return catalog.recipes.facts, catalog.recipes.factsErr
}

func (catalog *DefinitionCatalog) recipeFacts() (policy.RecipeFacts, error) {
	var facts policy.RecipeFacts
	names := make([]string, 0, len(catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]))
	for name := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		names = append(names, name)
	}
	slices.Sort(names)
	for _, name := range names {
		recipe, err := catalog.Recipe(name)
		if err != nil {
			return facts, err
		}
		install, ok, err := catalog.materialInstall(recipe)
		if err != nil {
			return facts, err
		}
		if ok {
			facts.MaterialInstalls = append(facts.MaterialInstalls, install)
		}
		benches, err := catalog.recipeBenches(recipe)
		if err != nil {
			return facts, err
		}
		for _, bench := range benches {
			work, found, err := catalog.billWorkType(recipe, bench)
			if err != nil {
				return facts, err
			}
			if found {
				if facts.BillWork == nil {
					facts.BillWork = map[string]policy.WorkType{}
				}
				facts.BillWork[name] = policy.WorkType(work)
				break
			}
		}
	}
	return facts, nil
}

// materialInstall reads a recipe as an install of a part made straight from a
// stuff: it adds a hediff, consumes one def that no body part category holds,
// and the hediff is what removing the part gives that same def back (a peg leg
// takes a log and a removed peg leg is a log). A body part item install, a
// denture that takes only medicine and every other recipe are not.
func (catalog *DefinitionCatalog) materialInstall(recipe *d.RecipeDef) (policy.MaterialInstall, bool, error) {
	if recipe.GetAddsHediff() == "" {
		return policy.MaterialInstall{}, false, nil
	}
	hediff := DefRow[*d.HediffDef](catalog, recipe.GetAddsHediff())
	if hediff == nil {
		return policy.MaterialInstall{}, false, contract("recipe %s adds hediff %s the catalog has no row for", recipe.GetDefName(), recipe.GetAddsHediff())
	}
	if hediff.GetSpawnThingOnRemoved() == "" {
		return policy.MaterialInstall{}, false, nil
	}
	item, err := catalog.InstallItem(recipe.GetDefName())
	if err != nil || item != "" {
		return policy.MaterialInstall{}, false, err
	}
	for _, slot := range recipe.GetIngredients() {
		filter := slot.GetValue().GetFilter()
		if len(filter.GetThingDefs()) != 1 || len(filter.GetCategories()) != 0 || filter.GetThingDefs()[0] != hediff.GetSpawnThingOnRemoved() {
			continue
		}
		material := filter.GetThingDefs()[0]
		price, err := catalog.StatValue(material, "", statMarketValue)
		if err != nil {
			return policy.MaterialInstall{}, false, err
		}
		bodies := slices.Clone(recipe.GetAppliedOnFixedBodyParts())
		if len(bodies) == 0 {
			return policy.MaterialInstall{}, false, contract("recipe %s installs %s on no fixed body part", recipe.GetDefName(), hediff.GetDefName())
		}
		return policy.MaterialInstall{
			Recipe: recipe.GetDefName(), Bodies: bodies, Part: hediff.GetDefName(), Work: float64(recipe.GetWorkAmount()),
			Material: material, Value: float64(price) * float64(slot.GetValue().GetCount()),
		}, true, nil
	}
	return policy.MaterialInstall{}, false, nil
}
