package observation

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ClothingGarments are the garments the colonists' outfits allow that cover a
// core body-part group, each with its first available recipe's ingredient
// slots (policy.ClothingMaterials prices the outfit from them). A definition
// without a known available recipe is left out; an unread census or catalog
// yields none.
func ClothingGarments(defs GearDefinitions, gear domain.Fact[policy.GearObservation]) []policy.ClothingGarment {
	observed, known := gear.Value()
	finished, researchKnown := defs.Finished.Value()
	if !known || !researchKnown || defs.Catalog == nil {
		return nil
	}
	allowed := map[string]bool{}
	for _, p := range observed.Pawns {
		if state, ok := p.Policy.Value(); ok {
			for _, name := range policy.RoleApparelDefinitions(policy.DeriveGearRole(state.Role), state) {
				allowed[name] = true
			}
		}
	}
	var out []policy.ClothingGarment
	for name := range allowed {
		_, apparel := apparelRow(defs.Catalog, name)
		groups := apparel.GetBodyPartGroups()
		if !slices.ContainsFunc(policy.GearCoreGroups, func(g string) bool { return slices.Contains(groups, g) }) {
			continue
		}
		hosts, err := defs.Catalog.RecipeHosts(name, finished)
		if err != nil {
			return nil
		}
		for _, host := range hosts {
			if slots, ok := host.Ingredients.Value(); ok && host.Available {
				out = append(out, policy.ClothingGarment{Definition: policy.Resource(name), Groups: slices.Clone(groups), Slots: slots})
				break
			}
		}
	}
	slices.SortFunc(out, func(a, b policy.ClothingGarment) int {
		return strings.Compare(string(a.Definition), string(b.Definition))
	})
	return out
}
