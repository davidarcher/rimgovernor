package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// categoryBodyParts is the thing category root of every body part item, the
// natural organs and the artificial parts.
const categoryBodyParts = "BodyParts"

// InstallItem is the one body part item the recipe consumes: the part an
// install recipe installs (InstallBionicArm consumes BionicArm, the natural
// install of a kidney consumes Kidney). It is the recipe's ingredient slot that
// names a single def sitting within the body part categories; a recipe with no
// such slot (a peg leg is made of logs) installs no item. A nil catalog names
// none; a recipe with no row is an error.
func (catalog *DefinitionCatalog) InstallItem(recipe string) (policy.Resource, error) {
	if catalog == nil {
		return "", nil
	}
	row, err := catalog.Recipe(recipe)
	if err != nil {
		return "", err
	}
	var item policy.Resource
	for _, slot := range row.GetIngredients() {
		filter := slot.GetValue().GetFilter()
		if len(filter.GetThingDefs()) != 1 || len(filter.GetCategories()) != 0 {
			continue
		}
		name := filter.GetThingDefs()[0]
		thing, err := catalog.thingRow(name)
		if err != nil {
			return "", contract("recipe %s: ingredient: %v", recipe, err)
		}
		within, err := catalog.categoriesWithin(name, thing)
		if err != nil {
			return "", err
		}
		if !within[categoryBodyParts] {
			continue
		}
		if item != "" {
			return "", contract("recipe %s: more than one body part ingredient (%s, %s)", recipe, item, name)
		}
		item = policy.Resource(name)
	}
	return item, nil
}
