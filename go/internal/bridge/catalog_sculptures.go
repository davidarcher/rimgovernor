package bridge

import (
	"cmp"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// statWorkToMake is the work a thing takes to make when its recipe names none.
const statWorkToMake = "WorkToMake"

// sculptures are the art recipes of the catalog: each recipe whose role
// is RoleSculpture, with the building it makes, the building's footprint and
// stuff cost, and the work the recipe takes (its own workAmount, else the
// product's WorkToMake). Smallest first: by stuff cost, then work, then name.
func (catalog *DefinitionCatalog) sculptures() ([]policy.Sculpture, error) {
	var out []policy.Sculpture
	for name := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		role, err := catalog.RecipeRole(name)
		if err != nil {
			return nil, err
		}
		if role != domain.RoleSculpture {
			continue
		}
		recipe, err := catalog.Recipe(name)
		if err != nil {
			return nil, err
		}
		product := recipe.GetProducts()[0].GetValue().GetThingDef()
		def, err := catalog.thingRow(product)
		if err != nil {
			return nil, err
		}
		work := float64(recipe.GetWorkAmount())
		if work <= 0 {
			for _, mod := range def.GetStatBases() {
				if stat := mod.GetValue(); stat.GetStat() == statWorkToMake {
					work = float64(stat.GetValue())
				}
			}
		}
		if work <= 0 || def.GetCostStuffCount() <= 0 {
			return nil, contract("sculpture recipe %s makes %s with no work or no stuff cost", name, product)
		}
		size := def.GetSize()
		out = append(out, policy.Sculpture{
			Recipe: name, Def: product, Size: domain.Cell{X: max(size.GetX(), 1), Z: max(size.GetZ(), 1)},
			Cost: int64(def.GetCostStuffCount()), Work: work,
		})
	}
	slices.SortFunc(out, func(a, b policy.Sculpture) int {
		return cmp.Or(cmp.Compare(a.Cost, b.Cost), cmp.Compare(a.Work, b.Work), cmp.Compare(a.Recipe, b.Recipe))
	})
	return out, nil
}
