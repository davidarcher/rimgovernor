package bridge

import (
	"slices"
	"strconv"
	"strings"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// RecipeBulk is whether the recipe is a bulk recipe: one of a group of recipes
// that take the same ingredient filters and make the same products, the one
// that makes more of them per trip to the bench (CookMealSimpleBulk against
// CookMealSimple, Make_PemmicanBulk against Make_Pemmican). The group is read
// from the rows, never from a name suffix. A nil catalog has no bulk recipes;
// a recipe with no row is an error.
func (catalog *DefinitionCatalog) RecipeBulk(name string) (bool, error) {
	if catalog == nil {
		return false, nil
	}
	if _, err := catalog.Recipe(name); err != nil {
		return false, err
	}
	catalog.recipes.bulkOnce.Do(func() { catalog.recipes.bulk, catalog.recipes.bulkErr = catalog.bulkRecipes() })
	return catalog.recipes.bulk[name], catalog.recipes.bulkErr
}

// bulkRecipes groups every recipe that makes products by its product defs and
// its ingredient slot filters; a recipe that makes more in total than the
// smallest of its group is bulk.
func (catalog *DefinitionCatalog) bulkRecipes() (map[string]bool, error) {
	type member struct {
		name  string
		total int32
	}
	groups := map[string][]member{}
	for name, row := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		recipe, ok := row.(*d.RecipeDef)
		if !ok || len(recipe.GetProducts()) == 0 || len(recipe.GetIngredients()) == 0 {
			continue
		}
		var key strings.Builder
		var total int32
		products := make([]string, 0, len(recipe.GetProducts()))
		for _, product := range recipe.GetProducts() {
			products = append(products, product.GetValue().GetThingDef())
			total += product.GetValue().GetCount()
		}
		slices.Sort(products)
		key.WriteString(strings.Join(products, ","))
		for _, slot := range recipe.GetIngredients() {
			data, err := proto.MarshalOptions{Deterministic: true}.Marshal(slot.GetValue().GetFilter())
			if err != nil {
				return nil, contract("recipe %s: ingredient filter: %v", name, err)
			}
			key.WriteString("|" + strconv.Itoa(len(data)) + ":" + string(data))
		}
		groups[key.String()] = append(groups[key.String()], member{name, total})
	}
	bulk := map[string]bool{}
	for _, members := range groups {
		least := members[0].total
		for _, m := range members {
			least = min(least, m.total)
		}
		for _, m := range members {
			if m.total > least {
				bulk[m.name] = true
			}
		}
	}
	return bulk, nil
}
