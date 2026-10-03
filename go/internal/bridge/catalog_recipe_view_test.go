package bridge

import (
	"slices"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

func recipeNames(catalog *DefinitionCatalog) []string {
	var names []string
	for name := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}

// Every recipe of the full catalog reads without an error: its ingredients
// (generated recipes included), mech kind, benches, work and meal facts. Only
// a drug administration, which makes nothing and whose allowed drug no field of
// the row records, has unknown ingredients.
func TestRecipeViewReadsEveryVanillaRecipe(t *testing.T) {
	catalog := fullCatalog(t)
	names := recipeNames(catalog)
	if len(names) != 442 {
		t.Fatalf("%d recipes", len(names))
	}
	for _, name := range names {
		row, err := catalog.Recipe(name)
		if err != nil {
			t.Fatal(err)
		}
		ingredients, err := catalog.RecipeIngredients(name)
		if err != nil {
			t.Errorf("%s ingredients: %v", name, err)
		} else if _, known := ingredients.Value(); !known && !strings.HasPrefix(name, "Administer_") {
			t.Errorf("%s ingredients unknown", name)
		}
		if _, err := catalog.RecipeMechKind(name); err != nil {
			t.Errorf("%s mech kind: %v", name, err)
		}
		benches, err := catalog.recipeBenches(row)
		if err != nil {
			t.Errorf("%s benches: %v", name, err)
		}
		for _, bench := range benches {
			if _, err := catalog.RecipeWork(name, bench); err != nil {
				t.Errorf("%s work at %s: %v", name, bench, err)
			}
			if _, err := catalog.RecipeMealFacts(name, bench); err != nil {
				t.Errorf("%s meal facts at %s: %v", name, bench, err)
			}
		}
	}
}

func TestRecipeViewGeneratedRecipesTakeTheirProductsCost(t *testing.T) {
	catalog := fullCatalog(t)
	// A stuff cost allows every stuff that can make the product; the volume
	// getter scales a small-volume stuff's count (gold and silver count tenfold).
	got, err := catalog.RecipeIngredients("Make_MeleeWeapon_Club")
	if err != nil {
		t.Fatal(err)
	}
	slots, known := got.Value()
	if !known || len(slots) != 1 {
		t.Fatal(got)
	}
	counts := map[policy.Resource]int64{}
	for _, amount := range slots[0] {
		counts[amount.Resource] = amount.Count
	}
	if counts["WoodLog"] != 40 || counts["Steel"] != 40 || counts["Gold"] != 400 || counts["Cloth"] != 0 {
		t.Errorf("club counts %v", counts)
	}
	// A costList entry allows exactly its def.
	got, err = catalog.RecipeIngredients("Make_Bow_Short")
	if err != nil {
		t.Fatal(err)
	}
	if slots, known = got.Value(); !known || len(slots) != 1 || !slices.Equal(slots[0], []policy.Amount{{Resource: "WoodLog", Count: 30}}) {
		t.Errorf("short bow %v", slots)
	}
	// A hand-written recipe's filter is read as written.
	if _, err := catalog.RecipeIngredients("Make_Nothing"); err == nil {
		t.Error("a recipe with no row")
	}
}

func TestRecipeViewWorkIsTheBenchsDoBillGiver(t *testing.T) {
	catalog := fullCatalog(t)
	work, err := catalog.RecipeWork("Make_Apparel_Parka", "HandTailoringBench")
	if err != nil {
		t.Fatal(err)
	}
	if reqs, known := work.Value(); !known || len(reqs) != 1 || reqs[0].Work != "Tailoring" || reqs[0].Skill != "Crafting" || reqs[0].Minimum != 0 {
		t.Errorf("parka at the tailor bench: %v", reqs)
	}
	// No giver serves a bench that is not one.
	if work, err = catalog.RecipeWork("Make_Apparel_Parka", "Stool"); err != nil {
		t.Fatal(err)
	} else if _, known := work.Value(); known {
		t.Error("work at a bench no giver serves")
	}
}

func TestRecipeViewMealFacts(t *testing.T) {
	catalog := fullCatalog(t)
	fine, err := catalog.RecipeMealFacts("CookMealFine", "ElectricStove")
	if err != nil {
		t.Fatal(err)
	}
	if power, _ := fine.NeedsPower.Value(); !power {
		t.Error("an electric stove needs power")
	}
	mood, _ := fine.Mood.Value()
	floor, _ := fine.CookSkillFloor.Value()
	classes, known := fine.IngredientClasses.Value()
	if mood != 5 || floor != 6 || !known || len(classes) != 2 || !slices.Equal(classes[1].Alternatives, []policy.FoodIngredientClass{policy.IngredientVegetable}) {
		t.Errorf("fine meal %+v", fine)
	}
	if efficiency, known := fine.NutrientEfficiency.Value(); !known || efficiency < 1.79 || efficiency > 1.81 {
		t.Errorf("fine meal efficiency %v", efficiency)
	}
	if work, known := fine.WorkPerNutrition.Value(); !known || work != 500 {
		t.Errorf("fine meal work per nutrition %v", work)
	}
	// The meals the retired bills/census acceptance expected of the game's own read.
	for name, want := range map[string]struct {
		mood, efficiency float64
		slots            int
	}{"CookMealSimple": {0, 1.8, 1}, "CookMealSimpleBulk": {0, 1.8, 1}, "CookMealFineBulk": {5, 1.8, 2}, "CookMealLavish": {12, 1, 2}, "CookMealLavishBulk": {12, 1, 2}} {
		facts, err := catalog.RecipeMealFacts(name, "ElectricStove")
		if err != nil {
			t.Fatal(err)
		}
		mood, _ := facts.Mood.Value()
		efficiency, _ := facts.NutrientEfficiency.Value()
		slots, _ := facts.IngredientClasses.Value()
		if mood != want.mood || efficiency < want.efficiency-1e-5 || efficiency > want.efficiency+1e-5 || len(slots) != want.slots {
			t.Errorf("%s: mood %v efficiency %v slots %v, want %+v", name, mood, efficiency, slots, want)
		}
	}
	if simple, _ := catalog.RecipeMealFacts("CookMealSimple", "ElectricStove"); true {
		if slots, _ := simple.IngredientClasses.Value(); len(slots) != 1 || !slices.Equal(slots[0].Alternatives, []policy.FoodIngredientClass{policy.IngredientAny}) {
			t.Errorf("simple meal takes any raw food: %v", slots)
		}
	}
	if fueled, err := catalog.RecipeMealFacts("CookMealFine", "FueledStove"); err != nil {
		t.Fatal(err)
	} else if power, _ := fueled.NeedsPower.Value(); power {
		t.Error("a fueled stove needs no power")
	}
	// Kibble is valued by volume: it has a mood and work, no raw slots.
	kibble, err := catalog.RecipeMealFacts("Make_Kibble", "ElectricStove")
	if err != nil {
		t.Fatal(err)
	}
	if _, known := kibble.IngredientClasses.Value(); known {
		t.Error("kibble has no raw slots")
	}
	if _, known := kibble.WorkPerNutrition.Value(); !known {
		t.Error("kibble work per nutrition")
	}
	if none, err := (*DefinitionCatalog)(nil).RecipeMealFacts("CookMealFine", "ElectricStove"); err != nil {
		t.Fatal(err)
	} else if _, known := none.Mood.Value(); known {
		t.Error("a nil catalog knows nothing")
	}
}

func TestRecipeViewMechKindAndHosts(t *testing.T) {
	catalog := fullCatalog(t)
	if kind, err := catalog.RecipeMechKind("Lancer"); err != nil || kind != "Mech_Lancer" {
		t.Errorf("lancer kind %q %v", kind, err)
	}
	if kind, err := catalog.RecipeMechKind("Make_Bow_Short"); err != nil || kind != "" {
		t.Errorf("bow kind %q %v", kind, err)
	}
	hosts, err := catalog.RecipeHosts("Apparel_Parka", map[string]bool{})
	if err != nil || len(hosts) != 1 || !hosts[0].Available || !slices.Equal(hosts[0].Benches, []string{"ElectricTailoringBench", "HandTailoringBench"}) {
		t.Fatalf("parka hosts %+v %v", hosts, err)
	}
	// Availability is the finished research of the recipe's prerequisites.
	var gated policy.RecipeHost
	for _, name := range recipeNames(catalog) {
		row, _ := catalog.Recipe(name)
		if row.GetResearchPrerequisite() == "" || len(row.GetProducts()) != 1 || len(row.GetMemePrerequisitesAny()) > 0 {
			continue
		}
		product := row.GetProducts()[0].GetValue().GetThingDef()
		found, err := catalog.RecipeHosts(product, map[string]bool{})
		if err != nil {
			t.Fatal(err)
		}
		for _, host := range found {
			if host.Definition == name {
				gated = host
			}
		}
		if gated.Definition != "" {
			break
		}
	}
	if gated.Definition == "" || gated.Available || len(gated.Research) == 0 {
		t.Fatalf("no research-gated recipe found: %+v", gated)
	}
	again, err := catalog.RecipeHosts(string(gated.Products[0]), map[string]bool{gated.Research[0]: true})
	if err != nil {
		t.Fatal(err)
	}
	for _, host := range again {
		if host.Definition == gated.Definition && len(gated.Research) == 1 && !host.Available {
			t.Errorf("%s stays unavailable after %s", host.Definition, gated.Research[0])
		}
	}
	// A recipe gated by ideology is never offered: only the game can say.
	if hosts, err := catalog.RecipeHosts("Apparel_Blindfold", map[string]bool{}); err != nil || len(hosts) != 0 {
		t.Errorf("blindfold hosts %+v %v", hosts, err)
	}
	if _, err := (*DefinitionCatalog)(nil).RecipeHosts("Steel", nil); err == nil {
		t.Error("a nil catalog has no hosts")
	}
}
