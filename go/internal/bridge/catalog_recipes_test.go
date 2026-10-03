package bridge

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func slot(filter *d.ThingFilter, count float32) *d.Opt_IngredientCount {
	return &d.Opt_IngredientCount{Value: &d.IngredientCount{Filter: filter, Count: count}}
}

func product(def string, count int32) *d.Opt_ThingDefCountClass {
	return &d.Opt_ThingDefCountClass{Value: &d.ThingDefCountClass{ThingDef: def, Count: count}}
}

func rottable() *d.Opt_CompPropertiesAny {
	return &d.Opt_CompPropertiesAny{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Rottable{CompProperties_Rottable: &d.CompProperties_Rottable{DaysToRotStart: 4}}}}
}

func artComp() *d.Opt_CompPropertiesAny {
	return &d.Opt_CompPropertiesAny{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Art{CompProperties_Art: &d.CompProperties_Art{}}}}
}

// recipeFixture lays the game's own recipes out as rows (the shapes of
// Recipes_Cremation.xml, Recipes_Butchery.xml, Recipes_Food and Buildings_Art).
func recipeFixture() *DefinitionCatalog {
	const thing = "Verse.ThingWithComps"
	corpses := &d.ThingFilter{Categories: []string{"Corpses"}}
	recipes := []*d.RecipeDef{
		{DefName: "ButcherCorpseFlesh", WorkerCounterClass: "Verse.RecipeWorkerCounter_ButcherAnimals", SpecialProducts: []d.SpecialProductType{d.SpecialProductType_SPECIAL_PRODUCT_TYPE_BUTCHERY}, Ingredients: []*d.Opt_IngredientCount{slot(corpses, 1)}},
		{DefName: "CremateCorpse", Ingredients: []*d.Opt_IngredientCount{slot(corpses, 1)}},
		{DefName: "BurnApparel", Ingredients: []*d.Opt_IngredientCount{slot(&d.ThingFilter{Categories: []string{"Apparel"}}, 1)}},
		{DefName: "CookMealSimple", Products: []*d.Opt_ThingDefCountClass{product("MealSimple", 1)}, Ingredients: []*d.Opt_IngredientCount{slot(&d.ThingFilter{Categories: []string{"FoodRaw"}}, 0.5)}},
		{DefName: "CookMealSurvival", Products: []*d.Opt_ThingDefCountClass{product("MealSurvivalPack", 1)}},
		{DefName: "Make_SculptureSmall", Products: []*d.Opt_ThingDefCountClass{product("SculptureSmall", 1)}},
		{DefName: "Make_Steel", Products: []*d.Opt_ThingDefCountClass{product("Steel", 1)}},
		{DefName: "Make_Stool", Products: []*d.Opt_ThingDefCountClass{product("Stool", 1)}},
	}
	rows := map[string]proto.Message{}
	for _, r := range recipes {
		rows[r.DefName] = r
	}
	categories := map[string]proto.Message{
		"Root":        &d.ThingCategoryDef{DefName: "Root"},
		"Corpses":     &d.ThingCategoryDef{DefName: "Corpses", Parent: "Root"},
		"Apparel":     &d.ThingCategoryDef{DefName: "Apparel", Parent: "Root"},
		"FoodRaw":     &d.ThingCategoryDef{DefName: "FoodRaw", Parent: "Root"},
		"CorpsesMech": &d.ThingCategoryDef{DefName: "CorpsesMech", Parent: "Corpses"},
	}
	things := map[string]*d.ThingDef{
		"Corpse_Human":     {DefName: "Corpse_Human", ThingClass: "Verse.Corpse", ThingCategories: []string{"Corpses"}},
		"Corpse_Mech":      {DefName: "Corpse_Mech", ThingClass: "Verse.Corpse", ThingCategories: []string{"CorpsesMech"}},
		"Apparel_Parka":    {DefName: "Apparel_Parka", ThingClass: "RimWorld.Apparel", ThingCategories: []string{"Apparel"}},
		"MealSimple":       {DefName: "MealSimple", ThingClass: thing, Comps: []*d.Opt_CompPropertiesAny{rottable()}},
		"MealSurvivalPack": {DefName: "MealSurvivalPack", ThingClass: thing},
		"SculptureSmall":   {DefName: "SculptureSmall", ThingClass: thing, Comps: []*d.Opt_CompPropertiesAny{artComp()}},
		"Steel":            {DefName: "Steel", ThingClass: thing},
		"Stool":            {DefName: "Stool", ThingClass: thing},
	}
	facts := map[string]*o.ThingDefFacts{
		"MealSimple":       {DefName: "MealSimple", FoodKind: o.FoodKind_FOOD_KIND_MEAL_SIMPLE.Enum(), MealIngredients: o.MealIngredients_MEAL_INGREDIENTS_ANY.Enum()},
		"MealSurvivalPack": {DefName: "MealSurvivalPack", FoodKind: o.FoodKind_FOOD_KIND_MEAL_SIMPLE.Enum(), MealIngredients: o.MealIngredients_MEAL_INGREDIENTS_ANY.Enum()},
		"SculptureSmall":   {DefName: "SculptureSmall"},
		"Steel":            {DefName: "Steel"},
		"Stool":            {DefName: "Stool"},
	}
	return &DefinitionCatalog{
		ThingDefs: things,
		Defs: map[protoreflect.FullName]map[string]proto.Message{
			(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName():        rows,
			(&d.ThingCategoryDef{}).ProtoReflect().Descriptor().FullName(): categories,
		},
		classBases: map[string][]string{
			thing: nil, "RimWorld.Apparel": nil, "Verse.Corpse": nil,
			"Verse.RecipeWorkerCounter_ButcherAnimals": {"Verse.RecipeWorkerCounter"}, "Verse.RecipeWorkerCounter": nil,
		},
		thingFacts: facts,
	}
}

// TestRecipeRolesComeFromTheRows (#1721): the butcher recipe is the one with
// the butcher counter, cremation is the corpse-consuming recipe that makes
// nothing (burning apparel is not), a sculpture makes an art building and an
// ordinary meal is a perishable meal; a meal that never rots is a reserve.
func TestRecipeRolesComeFromTheRows(t *testing.T) {
	catalog := recipeFixture()
	for recipe, want := range map[string]domain.RecipeRole{
		"ButcherCorpseFlesh":  domain.RoleButcherFlesh,
		"CremateCorpse":       domain.RoleCremation,
		"BurnApparel":         domain.RoleNone,
		"CookMealSimple":      domain.RoleOrdinaryMeal,
		"CookMealSurvival":    domain.RoleNone,
		"Make_SculptureSmall": domain.RoleSculpture,
		"Make_Steel":          domain.RoleNone,
	} {
		got, err := catalog.RecipeRole(recipe)
		if err != nil || got != want {
			t.Errorf("%s: role %q, %v; want %q", recipe, got, err, want)
		}
	}
	if _, err := catalog.RecipeRole("Make_Nothing"); err == nil || !strings.Contains(err.Error(), "no recipe row") {
		t.Errorf("a recipe with no row: %v", err)
	}
	if role, err := (*DefinitionCatalog)(nil).RecipeRole("anything"); err != nil || role != domain.RoleNone {
		t.Errorf("nil catalog: %q %v", role, err)
	}
	name, ok, err := catalog.RecipeWithRole(domain.RoleCremation, []string{"BurnApparel", "CremateCorpse"})
	if err != nil || !ok || name != "CremateCorpse" {
		t.Errorf("RecipeWithRole = %q %v %v", name, ok, err)
	}
	if _, ok, err := catalog.RecipeWithRole(domain.RoleCremation, []string{"BurnApparel"}); ok || err != nil {
		t.Errorf("no cremation recipe on the bench: %v %v", ok, err)
	}
}

// TestFilterAcceptsReadsTheFourVanillaFields (#1721): listed defs and
// categories (parents included) allow, disallowed categories and defs veto,
// and a filter with a field the evaluator does not model is refused.
func TestFilterAcceptsReadsTheFourVanillaFields(t *testing.T) {
	catalog := recipeFixture()
	filter := &d.ThingFilter{Categories: []string{"Corpses"}, DisallowedCategories: []string{"CorpsesMech"}, ThingDefs: []string{"Steel"}, DisallowedThingDefs: []string{"Steel"}}
	for def, want := range map[string]bool{"Corpse_Human": true, "Corpse_Mech": false, "Steel": false, "Apparel_Parka": false} {
		got, err := catalog.FilterAccepts(filter, def)
		if err != nil || got != want {
			t.Errorf("%s: %v, %v; want %v", def, got, err, want)
		}
	}
	if got, err := catalog.FilterAccepts(&d.ThingFilter{ThingDefs: []string{"Stool"}}, "Stool"); err != nil || !got {
		t.Errorf("listed def: %v %v", got, err)
	}
	if _, err := catalog.FilterAccepts(&d.ThingFilter{Categories: []string{"Corpses"}, SpecialFiltersToAllow: []string{"AllowFresh"}}, "Corpse_Human"); err == nil || !strings.Contains(err.Error(), "specialFilters") {
		t.Errorf("special filter accepted: %v", err)
	}
	if _, err := catalog.FilterAccepts(&d.ThingFilter{DisallowCheaperThan: 5}, "Steel"); err == nil {
		t.Error("disallowCheaperThan accepted")
	}
	if got, err := catalog.FilterAccepts(&d.ThingFilter{DisallowCheaperThan: -3.4e38, Categories: []string{"Corpses"}}, "Corpse_Human"); err != nil || !got {
		t.Errorf("default disallowCheaperThan: %v %v", got, err)
	}
}
