package bridge

import (
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Recipe rows (#1721). What a recipe does is read from its RecipeDef row, never
// from its defName: the butcher recipe is the one whose worker counter is the
// game's butcher counter, a sculpture makes an art building, an ordinary meal makes a perishable meal.
// A recipe the catalog has no row for, or a row the rules cannot classify (a
// filter field the evaluator does not model), is a named error.

// The game classes the recipe roles are read by.
const (
	classButcherCounter = "Verse.RecipeWorkerCounter_ButcherAnimals"
)

// recipeCache holds the derived recipe facts of one catalog.
type recipeCache struct {
	mu    sync.Mutex
	roles map[string]domain.RecipeRole
	// bulk is the set of bulk recipes, built once.
	bulkOnce sync.Once
	bulk     map[string]bool
	bulkErr  error
	// facts are the recipe facts the planners read, built once.
	factsOnce sync.Once
	facts     policy.RecipeFacts
	factsErr  error
}

// Recipe is name's RecipeDef row; it is an error when the catalog has none.
func (catalog *DefinitionCatalog) Recipe(name string) (*d.RecipeDef, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	row := DefRow[*d.RecipeDef](catalog, name)
	if row == nil {
		return nil, contract("catalog has no recipe row for %s", name)
	}
	return row, nil
}

// RecipeRole is what the recipe does by its row, RoleNone for a recipe the
// planners do not single out. A nil catalog has no roles (a test without a
// loaded game); a catalog with no row for the recipe is an error.
func (catalog *DefinitionCatalog) RecipeRole(name string) (domain.RecipeRole, error) {
	if catalog == nil {
		return domain.RoleNone, nil
	}
	catalog.recipes.mu.Lock()
	defer catalog.recipes.mu.Unlock()
	if role, ok := catalog.recipes.roles[name]; ok {
		return role, nil
	}
	row, err := catalog.Recipe(name)
	if err != nil {
		return domain.RoleNone, err
	}
	role, err := catalog.deriveRecipeRole(row)
	if err != nil {
		return domain.RoleNone, err
	}
	if catalog.recipes.roles == nil {
		catalog.recipes.roles = map[string]domain.RecipeRole{}
	}
	catalog.recipes.roles[name] = role
	return role, nil
}

// RecipeWithRole is the one recipe among names whose role is role: the recipes
// a bench offers, so a planner finds the butcher recipe of a bench without
// naming it. None, and more than one, are reported by ok and an error
// respectively.
func (catalog *DefinitionCatalog) RecipeWithRole(role domain.RecipeRole, names []string) (string, bool, error) {
	found := ""
	for _, name := range names {
		got, err := catalog.RecipeRole(name)
		if err != nil {
			return "", false, err
		}
		if got != role {
			continue
		}
		if found != "" {
			return "", false, contract("recipes %s and %s both have the role %s", found, name, role)
		}
		found = name
	}
	return found, found != "", nil
}

func (catalog *DefinitionCatalog) deriveRecipeRole(row *d.RecipeDef) (domain.RecipeRole, error) {
	if class := row.GetWorkerCounterClass(); class != "" {
		butcher, err := catalog.ClassIsA(class, classButcherCounter)
		if err != nil {
			return domain.RoleNone, err
		}
		if butcher {
			return domain.RoleButcherFlesh, nil
		}
	}
	if len(row.GetProducts()) == 1 && len(row.GetSpecialProducts()) == 0 {
		product := row.GetProducts()[0].GetValue().GetThingDef()
		def, err := catalog.thingRow(product)
		if err != nil {
			return domain.RoleNone, err
		}
		if compOf(def, (*d.CompPropertiesAny).GetCompProperties_Art) != nil {
			return domain.RoleSculpture, nil
		}
	}
	meal, err := catalog.makesOrdinaryMeal(row)
	if err != nil {
		return domain.RoleNone, err
	}
	if meal {
		return domain.RoleOrdinaryMeal, nil
	}
	return domain.RoleNone, nil
}

// makesOrdinaryMeal is whether every product is a perishable meal of the
// simple, fine or lavish kind; a meal that never rots is a reserve, not part
// of the ordinary tier family.
func (catalog *DefinitionCatalog) makesOrdinaryMeal(row *d.RecipeDef) (bool, error) {
	if len(row.GetProducts()) == 0 {
		return false, nil
	}
	for _, product := range row.GetProducts() {
		name := product.GetValue().GetThingDef()
		facts, err := catalog.thingFactsRow(name)
		if err != nil {
			return false, err
		}
		switch facts.GetFoodKind() {
		case o.FoodKind_FOOD_KIND_MEAL_SIMPLE, o.FoodKind_FOOD_KIND_MEAL_FINE, o.FoodKind_FOOD_KIND_MEAL_LAVISH:
		default:
			return false, nil
		}
		if facts.FoodKind == nil {
			return false, nil
		}
		_, perishable, err := catalog.RotDays(name)
		if err != nil {
			return false, err
		}
		if !perishable {
			return false, nil
		}
	}
	return true, nil
}

// FilterAccepts is whether a ThingFilter allows the def, by the game's own
// resolution (ThingFilter.ResolveReferences): the listed defs and every def
// within a listed category, less every def within a disallowed category and
// every disallowed def. The vanilla recipe slots use only those four fields;
// a filter that sets any other restriction (special filters, trade tags,
// quality, comps, preferability...) is refused rather than half-read.
func (catalog *DefinitionCatalog) FilterAccepts(filter *d.ThingFilter, def string) (bool, error) {
	if filter == nil {
		return false, nil
	}
	if field := unmodelledFilterField(filter); field != "" {
		return false, contract("thing filter sets %s, which the recipe evaluator does not model", field)
	}
	row, err := catalog.thingRow(def)
	if err != nil {
		return false, err
	}
	within, err := catalog.categoriesWithin(def, row)
	if err != nil {
		return false, err
	}
	allowed := slices.Contains(filter.GetThingDefs(), def)
	for _, category := range filter.GetCategories() {
		allowed = allowed || within[category]
	}
	for _, category := range filter.GetDisallowedCategories() {
		if within[category] {
			return false, nil
		}
	}
	return allowed && !slices.Contains(filter.GetDisallowedThingDefs(), def), nil
}

// unmodelledFilterField names the first filter field the evaluator does not
// read that the filter sets, "" when it sets none.
func unmodelledFilterField(f *d.ThingFilter) string {
	switch {
	case f.GetOverrideRootDef() != "":
		return "overrideRootDef"
	case f.GetOnlySpecialFilters():
		return "onlySpecialFilters"
	case len(f.GetTradeTagsToAllow()) > 0 || len(f.GetTradeTagsToDisallow()) > 0:
		return "tradeTags"
	case len(f.GetThingSetMakerTagsToAllow()) > 0 || len(f.GetThingSetMakerTagsToDisallow()) > 0:
		return "thingSetMakerTags"
	case len(f.GetSpecialFiltersToAllow()) > 0 || len(f.GetSpecialFiltersToDisallow()) > 0:
		return "specialFilters"
	case len(f.GetStuffCategoriesToAllow()) > 0:
		return "stuffCategoriesToAllow"
	case len(f.GetAllowAllWhoCanMake()) > 0:
		return "allowAllWhoCanMake"
	case f.GetDisallowWorsePreferability() != d.FoodPreferability_FOOD_PREFERABILITY_UNDEFINED:
		return "disallowWorsePreferability"
	case f.GetDisallowInedibleByHuman():
		return "disallowInedibleByHuman"
	case f.GetDisallowDoesntProduceMeat():
		return "disallowDoesntProduceMeat"
	case f.GetDisallowMedicalDrugs():
		return "disallowMedicalDrugs"
	case f.GetDisallowNotEverStorable():
		return "disallowNotEverStorable"
	case f.GetAllowWithComp() != "" || f.GetDisallowWithComp() != "":
		return "comp filter"
	case f.GetDisallowCheaperThan() > 0:
		return "disallowCheaperThan"
	}
	return ""
}
