package policy

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testFoods = []Food{
	{"MealNutrientPaste", FoodKindMealAwful, MealAnyIngredients},
	{"MealSimple", FoodKindMealSimple, MealAnyIngredients},
	{"MealFine", FoodKindMealFine, MealAnyIngredients},
	{"MealFine_Meat", FoodKindMealFine, MealMeatOnly},
	{"MealFine_Veg", FoodKindMealFine, MealNonMeat},
	{"MealLavish", FoodKindMealLavish, MealAnyIngredients},
	{"Meat_Cow", FoodKindRawMeat, ""},
	{"Meat_Human", FoodKindHumanMeat, ""},
	{"Meat_Megaspider", FoodKindInsectMeat, ""},
	{"RawPotatoes", FoodKindVegetable, ""},
	{"RawFungus", FoodKindFungus, ""},
	{"Milk", FoodKindAnimalProduct, ""},
	{"Pemmican", FoodKindOther, ""},
	{"MealSurvivalPack", FoodKindMealFine, MealAnyIngredients},
}

// dietIdeology holds the precept defs the diet tests name; events are the
// game's HistoryEventDef names, as in the game's own precept comps.
var dietIdeology = domain.Known(ruleIdeoligion(
	PreceptDef{Name: "Cannibalism_Disapproved", Effects: []PreceptEffect{took("AteHumanMeat", -3)}},
	PreceptDef{Name: "Cannibalism_Preferred", Effects: []PreceptEffect{took("AteHumanMeat", 3)}},
	PreceptDef{Name: "MeatEating_Abhorrent", Effects: []PreceptEffect{took("AteMeat", -6)}},
	PreceptDef{Name: "MeatEating_NonMeat_Horrible", Effects: []PreceptEffect{took("AteNonMeat", -4)}},
	PreceptDef{Name: "InsectMeatEating_Loved", Effects: []PreceptEffect{took("AteInsectMeatDirect", 3)}},
	PreceptDef{Name: "FungusEating_Preferred", Effects: []PreceptEffect{took("AteFungus", 3)}},
	PreceptDef{Name: "FungusEating_Despised", Effects: []PreceptEffect{took("AteFungus", -3)}},
))

// eater is a pawn of an ideoligion holding the named precepts and, unless
// one is named, Cannibalism_Disapproved (every ideoligion holds one).
func eater(id string, traits []string, precepts ...string) WorkPawn {
	if len(precepts) > 0 && !slices.ContainsFunc(precepts, func(p string) bool { return strings.HasPrefix(p, "Cannibalism_") }) {
		precepts = append(precepts, "Cannibalism_Disapproved")
	}
	rows := []PawnTrait{}
	for _, t := range traits {
		rows = append(rows, testTrait(t, 0))
	}
	return WorkPawn{
		ID:              PawnID(id),
		Traits:          domain.Known(rows),
		PolicyInputs:    domain.Known(PawnPolicyInputs{Precepts: precepts}),
		FoodRestriction: domain.Known(FoodRestriction{PolicyID: "FoodPolicy_1"}),
		// Diet tests run in the mood tier; TestDietMoodTier covers it.
		HighExpectations: domain.Known(true),
	}
}

func without(defs ...string) []string {
	var out []string
	for _, f := range testFoods {
		if !slices.Contains(defs, f.Def) && !ReserveFoodDefinition(Resource(f.Def)) {
			out = append(out, f.Def)
		}
	}
	slices.Sort(out)
	return out
}

// Each diet allows its own foods (#1541): human meat only for cannibals,
// insect meat only where loved.
func TestDietFoods(t *testing.T) {
	for _, tc := range []struct {
		name string
		pawn WorkPawn
		want []string
	}{
		{"plain", eater("A", nil), without("Meat_Human", "Meat_Megaspider")},
		{"cannibal trait", eater("A", []string{"Cannibal"}), without("Meat_Megaspider")},
		{"cannibal precept", eater("A", nil, "Cannibalism_Preferred"), without("Meat_Megaspider")},
		{"insect lover", eater("A", nil, "InsectMeatEating_Loved"), without("Meat_Human")},
		{"vegetarian", eater("A", []string{"Cannibal"}, "MeatEating_Abhorrent", "InsectMeatEating_Loved"), without("Meat_Human", "Meat_Megaspider", "Meat_Cow", "MealFine_Meat")},
		{"carnivore", eater("A", nil, "MeatEating_NonMeat_Horrible"), without("Meat_Human", "Meat_Megaspider", "RawPotatoes", "RawFungus", "MealFine_Veg")},
		{"fungal carnivore", eater("A", nil, "MeatEating_NonMeat_Horrible", "FungusEating_Preferred"), without("Meat_Human", "Meat_Megaspider", "RawPotatoes", "MealFine_Veg")},
		{"fungus despised", eater("A", nil, "FungusEating_Despised"), without("Meat_Human", "Meat_Megaspider", "RawFungus")},
		{"ascetic", eater("A", []string{"Ascetic"}), without("Meat_Human", "Meat_Megaspider", "MealFine", "MealFine_Meat", "MealFine_Veg", "MealLavish")},
		{"gourmand", eater("A", []string{"Gourmand", "Ascetic"}), without("Meat_Human", "Meat_Megaspider")},
	} {
		diet, ok := PawnDiet(tc.pawn, dietIdeology)
		if got := DietFoods(diet, testFoods); !ok || !slices.Equal(got, tc.want) {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
	}
	unknown := eater("A", nil)
	unknown.Traits = domain.Unknown[[]PawnTrait]()
	if _, ok := PawnDiet(unknown, dietIdeology); ok {
		t.Error("unknown traits planned")
	}
}

// A cannibal and a vegetarian get different policies under their own
// names; a held matching policy owes nothing; shared names wait.
func TestDietPolicyChanges(t *testing.T) {
	pawns := []WorkPawn{eater("A", []string{"Cannibal"}), eater("B", nil, "MeatEating_Abhorrent"), eater("C", nil), eater("D", nil), eater("E", nil), eater("F", nil)}
	pawns[5].FoodRestriction = domain.Unknown[FoodRestriction]()
	names := []OwnedName{{"A", "Ann", 1}, {"B", "Bo", 2}, {"C", "Cy", 3}, {"D", "Dup", 4}, {"E", "dup", 5}, {"F", "Fay", 6}}
	policies := []FoodPolicyEntry{{ID: "FoodPolicy_3", Label: "Cy", Pawns: []PawnID{"C"}, Allowed: without("Meat_Human", "Meat_Megaspider")}}
	got := DietPolicyChanges(dietIdeology, pawns, nil, names, policies, testFoods, false)
	if len(got) != 2 {
		t.Fatalf("got %d changes: %+v", len(got), got)
	}
	ann, bo := got[0], got[1]
	if ann.Write == nil || ann.Write.Name() != "Ann" || !slices.Contains(ann.Write.Definitions(), "Meat_Human") || ann.Assign == nil {
		t.Errorf("cannibal: %+v", ann)
	}
	if bo.Write == nil || bo.Write.Name() != "Bo" || slices.Contains(bo.Write.Definitions(), "Meat_Cow") || slices.Equal(bo.Write.Definitions(), ann.Write.Definitions()) {
		t.Errorf("vegetarian: %+v", bo)
	}
	if name, ok := bo.Assign.FoodPolicy(); !ok || name != "Bo" {
		t.Errorf("vegetarian assignment: %+v", bo.Assign)
	}
	// Held but drifted contents are rewritten without a reassignment.
	policies[0].Allowed = []string{"MealSimple"}
	got = DietPolicyChanges(dietIdeology, pawns[2:3], nil, names, policies, testFoods, false)
	if len(got) != 1 || got[0].Write == nil || got[0].Assign != nil {
		t.Fatalf("drift: %+v", got)
	}
}

// Prisoners and slaves get paste and raw food within their diet; a tame
// animal kibble, hay and the raw food its race eats, never a meal (#1543).
func TestDietPolicyChangesNonColonists(t *testing.T) {
	foods := append(append([]Food(nil), testFoods...), Food{"Kibble", FoodKindKibble, ""}, Food{"Hay", FoodKindHay, ""})
	slave := eater("S", nil, "MeatEating_Abhorrent")
	slave.PolicyInputs = domain.Known(PawnPolicyInputs{Precepts: []string{"MeatEating_Abhorrent"}, GuestStatus: "Slave"})
	eaters := []FoodEater{
		{Pawn: "P", Traits: []string{"Cannibal"}},
		{Pawn: "H", Animal: true, Edible: []string{"Kibble", "Hay", "RawPotatoes", "MealSimple", "MealFine", "Milk"}},
		{Pawn: "W", Animal: true, Edible: []string{"Kibble", "Meat_Cow", "MealLavish"}},
	}
	names := []OwnedName{{"S", "Sal", 1}, {"P", "Pip", 2}, {"H", "Hoof", 3}, {"W", "Wolf", 4}}
	got := map[string][]string{}
	for _, c := range DietPolicyChanges(dietIdeology, []WorkPawn{slave}, eaters, names, nil, foods, false) {
		if c.Write == nil || c.Assign == nil {
			t.Fatalf("change %+v", c)
		}
		got[c.Write.Name()] = c.Write.Definitions()
	}
	for name, want := range map[string][]string{
		"Sal":  {"MealNutrientPaste", "Milk", "RawFungus", "RawPotatoes"},
		"Pip":  {"MealNutrientPaste", "Meat_Cow", "Meat_Human", "Milk", "RawFungus", "RawPotatoes"},
		"Hoof": {"Hay", "Kibble", "Milk", "RawPotatoes"},
		"Wolf": {"Kibble", "Meat_Cow"},
	} {
		if !slices.Equal(got[name], want) {
			t.Errorf("%s: got %v, want %v", name, got[name], want)
		}
	}
}

// The mood tier (#1542): a pawn near its break threshold or under high
// expectations gets fine and lavish meals, a content pawn simple meals and
// paste; the travel reserve is never allowed.
func TestDietMoodTier(t *testing.T) {
	fine := []string{"MealFine", "MealFine_Meat", "MealFine_Veg", "MealLavish"}
	pawn := func(mood float64, high bool) WorkPawn {
		p := eater("A", nil)
		p.Mood, p.BreakThreshold, p.HighExpectations = domain.Known(mood), domain.Known(0.35), domain.Known(high)
		return p
	}
	for _, tc := range []struct {
		name string
		pawn WorkPawn
		want []string
	}{
		{"near break", pawn(0.4, false), without("Meat_Human", "Meat_Megaspider")},
		{"high expectations", pawn(0.9, true), without("Meat_Human", "Meat_Megaspider")},
		{"content", pawn(0.7, false), without(append(fine, "Meat_Human", "Meat_Megaspider")...)},
	} {
		diet, _ := PawnDiet(tc.pawn, dietIdeology)
		got := DietFoods(diet, testFoods)
		if !slices.Equal(got, tc.want) {
			t.Errorf("%s: got %v want %v", tc.name, got, tc.want)
		}
		if slices.Contains(got, "Pemmican") || slices.Contains(got, "MealSurvivalPack") {
			t.Errorf("%s: travel reserve allowed", tc.name)
		}
	}
}

// A colony short of other food eats the travel reserve (a fresh start has
// nothing else); otherwise it is kept for caravans.
func TestDietFoodsAllowsTheReserveWhenShort(t *testing.T) {
	got := DietFoods(Diet{Reserve: true}, testFoods)
	if !slices.Contains(got, "Pemmican") || !slices.Contains(got, "MealSurvivalPack") {
		t.Fatalf("reserve withheld while short: %v", got)
	}
	if got := DietFoods(Diet{}, testFoods); slices.Contains(got, "Pemmican") {
		t.Fatalf("reserve allowed with food in hand: %v", got)
	}
}
