package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// armoryTechTier is the armory ladder rung of a native TechLevel: the
// research a recipe or its work table needs is neolithic, medieval
// (Smithing), industrial (Machining) or spacer (Fabrication). A level off the
// ladder (undefined, as the Anomaly projects state, or ultra and archotech)
// has no rung.
func armoryTechTier(level d.TechLevel) (policy.ArmoryTier, bool) {
	switch level {
	case d.TechLevel_TECH_LEVEL_ANIMAL, d.TechLevel_TECH_LEVEL_NEOLITHIC:
		return policy.ArmoryTierNeolithic, true
	case d.TechLevel_TECH_LEVEL_MEDIEVAL:
		return policy.ArmoryTierSmithing, true
	case d.TechLevel_TECH_LEVEL_INDUSTRIAL:
		return policy.ArmoryTierMachining, true
	case d.TechLevel_TECH_LEVEL_SPACER:
		return policy.ArmoryTierFabrication, true
	}
	return policy.ArmoryTierUnknown, false
}

// researchArmoryTier is the highest rung of the research projects' tech
// levels, neolithic for none; ok is false when a project is off the ladder.
func (catalog *DefinitionCatalog) researchArmoryTier(projects []string) (tier policy.ArmoryTier, ok bool, err error) {
	tier = policy.ArmoryTierNeolithic
	for _, name := range projects {
		if name == "" {
			continue
		}
		row := DefRow[*d.ResearchProjectDef](catalog, name)
		if row == nil {
			return policy.ArmoryTierUnknown, false, contract("catalog has no research project row for %s", name)
		}
		rung, on := armoryTechTier(row.GetTechLevel())
		if !on {
			return policy.ArmoryTierUnknown, false, nil
		}
		tier = max(tier, rung)
	}
	return tier, true, nil
}

// armamentProduct reports whether def is an ordinary weapon the armory may
// craft (policy.WeaponDef.Armament).
func (catalog *DefinitionCatalog) armamentProduct(def string) (bool, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return false, err
	}
	within, err := catalog.categoriesWithin(def, row)
	if err != nil {
		return false, err
	}
	if !within[categoryWeapons] {
		return false, nil
	}
	facts, err := catalog.WeaponOf(def)
	if err != nil {
		return false, err
	}
	return facts.Armament(), nil
}

// RecipeArmoryTier is the armory ladder rung of a recipe made at a work
// table, or ArmoryTierUnknown when the ladder does not model it: the highest
// of the tech levels of the recipe's research prerequisites and of the work
// table's, so a recipe needing no research still sits on the rung of the
// smithy that hosts it. Recipes that make no ordinary weapon, are gated by an
// ideology precept, or need research or a table off the tech ladder are
// unmodelled.
func (catalog *DefinitionCatalog) RecipeArmoryTier(recipe, bench string) (policy.ArmoryTier, error) {
	row, err := catalog.Recipe(recipe)
	if err != nil {
		return policy.ArmoryTierUnknown, err
	}
	if len(row.GetMemePrerequisitesAny()) > 0 || len(row.GetFactionPrerequisiteTags()) > 0 || row.GetFromIdeoBuildingPreceptOnly() {
		return policy.ArmoryTierUnknown, nil
	}
	products, err := recipeProductDefs(row)
	if err != nil {
		return policy.ArmoryTierUnknown, err
	}
	weapon := false
	for _, product := range products {
		if weapon, err = catalog.armamentProduct(string(product)); err != nil || weapon {
			break
		}
	}
	if err != nil || !weapon {
		return policy.ArmoryTierUnknown, err
	}
	tier, on, err := catalog.researchArmoryTier(append([]string{row.GetResearchPrerequisite()}, row.GetResearchPrerequisites()...))
	if err != nil || !on {
		return policy.ArmoryTierUnknown, err
	}
	table, err := catalog.thingRow(bench)
	if err != nil {
		return policy.ArmoryTierUnknown, err
	}
	tableTier, on, err := catalog.researchArmoryTier(table.GetResearchPrerequisites())
	if err != nil || !on {
		return policy.ArmoryTierUnknown, err
	}
	return max(tier, tableTier), nil
}

// ArmoryWeaponTier is the lowest rung at which any recipe on any player-built
// work table makes def, and whether the ladder models def at all.
func (catalog *DefinitionCatalog) ArmoryWeaponTier(def string) (policy.ArmoryTier, bool, error) {
	if catalog == nil {
		return policy.ArmoryTierUnknown, false, contract("no definition catalog")
	}
	best := policy.ArmoryTierUnknown
	for name, raw := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		row := raw.(*d.RecipeDef)
		products, err := recipeProductDefs(row)
		if err != nil {
			return policy.ArmoryTierUnknown, false, err
		}
		if !slices.Contains(products, policy.Resource(def)) {
			continue
		}
		benches, err := catalog.recipeBenches(row)
		if err != nil {
			return policy.ArmoryTierUnknown, false, err
		}
		for _, bench := range benches {
			tier, err := catalog.RecipeArmoryTier(name, bench)
			if err != nil {
				return policy.ArmoryTierUnknown, false, err
			}
			if tier != policy.ArmoryTierUnknown && (best == policy.ArmoryTierUnknown || tier < best) {
				best = tier
			}
		}
	}
	return best, best != policy.ArmoryTierUnknown, nil
}
