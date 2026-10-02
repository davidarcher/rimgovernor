package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FoodRestriction is the pawn's current food policy: its load id, allowed
// definitions and native diet eligibility. It is observation, never an
// explicit controller preference.
type FoodRestriction struct {
	PolicyID          string
	Allowed, Eligible []string
}

// FoodKind is what a food definition is for diets (#1541): a meal tier,
// meat by source, a vegetable, fungus, an animal product or other.
type FoodKind string

const (
	FoodKindMealAwful     FoodKind = "meal_awful"
	FoodKindMealSimple    FoodKind = "meal_simple"
	FoodKindMealFine      FoodKind = "meal_fine"
	FoodKindMealLavish    FoodKind = "meal_lavish"
	FoodKindRawMeat       FoodKind = "raw_meat"
	FoodKindHumanMeat     FoodKind = "human_meat"
	FoodKindInsectMeat    FoodKind = "insect_meat"
	FoodKindVegetable     FoodKind = "vegetable"
	FoodKindFungus        FoodKind = "fungus"
	FoodKindAnimalProduct FoodKind = "animal_product"
	FoodKindOther         FoodKind = "other"
)

// MealIngredients is what a meal definition is made of: meat only, no
// meat, or either; empty for a food that is no meal.
type MealIngredients string

const (
	MealAnyIngredients MealIngredients = "any"
	MealMeatOnly       MealIngredients = "meat"
	MealNonMeat        MealIngredients = "non_meat"
)

// Food is one food ThingDef a food policy can allow.
type Food struct {
	Def         string
	Kind        FoodKind
	Ingredients MealIngredients
}

// FoodPolicyEntry is one native food policy: its load id, label, holders
// and allowed food definitions.
type FoodPolicyEntry struct {
	ID, Label string
	Pawns     []PawnID
	Allowed   []string
}

// FoodPolicyChange is what one pawn's food policy owes: the contents to
// write (nil when the policy labelled with its short name already allows
// exactly them) and the assignment (nil when it already holds it).
type FoodPolicyChange struct {
	Write  *domain.FoodPolicy
	Assign *domain.PawnSettings
}

// Diet is what a pawn's ideoligion precepts and traits make of food.
type Diet struct {
	// Cannibal: the Cannibal trait or a cannibalism precept that accepts
	// human meat.
	Cannibal bool
	// InsectMeat: a precept that loves insect meat; everyone else minds it.
	InsectMeat bool
	// Vegetarian and Carnivore are the meat-eating precepts.
	Vegetarian, Carnivore bool
	// Fungal: fungus preferred; NoFungus: fungus despised.
	Fungal, NoFungus  bool
	Ascetic, Gourmand bool
	// FineMeals: the mood tier (#1542) allows fine and lavish meals, for a
	// pawn near its mental break threshold or with high expectations.
	FineMeals bool
}

// moodTierMargin is how far above the minor break threshold a pawn's mood
// still counts as near a break (#1542).
const moodTierMargin = 0.1

// travelReserve are the foods kept for caravans: never eaten at home
// (#1542).
var travelReserve = []string{"Pemmican", "MealSurvivalPack"}

// fineMeals is the mood tier: near the minor break threshold or under high
// expectations; unknown mood reads count as content.
func fineMeals(pawn WorkPawn) bool {
	if high, ok := pawn.HighExpectations.Value(); ok && high {
		return true
	}
	mood, mk := pawn.Mood.Value()
	threshold, tk := pawn.BreakThreshold.Value()
	return mk && tk && mood < threshold+moodTierMargin
}

var (
	cannibalPrecepts   = []string{"Cannibalism_Acceptable", "Cannibalism_Preferred", "Cannibalism_RequiredStrong", "Cannibalism_RequiredRavenous"}
	vegetarianPrecepts = []string{"MeatEating_Disapproved", "MeatEating_Horrible", "MeatEating_Abhorrent"}
	carnivorePrecepts  = []string{"MeatEating_NonMeat_Disapproved", "MeatEating_NonMeat_Horrible", "MeatEating_NonMeat_Abhorrent"}
)

// PawnDiet reads the pawn's diet; unknown while its traits or policy
// inputs (precepts) are.
func PawnDiet(pawn WorkPawn) (Diet, bool) {
	traits, ok := pawn.Traits.Value()
	inputs, known := pawn.PolicyInputs.Value()
	if !ok || !known {
		return Diet{}, false
	}
	var d Diet
	for _, t := range traits {
		switch t.Name {
		case "Cannibal":
			d.Cannibal = true
		case "Ascetic":
			d.Ascetic = true
		case "Gourmand":
			d.Gourmand = true
		}
	}
	for _, p := range inputs.Precepts {
		switch {
		case slices.Contains(cannibalPrecepts, p):
			d.Cannibal = true
		case p == "InsectMeatEating_Loved":
			d.InsectMeat = true
		case slices.Contains(vegetarianPrecepts, p):
			d.Vegetarian = true
		case slices.Contains(carnivorePrecepts, p):
			d.Carnivore = true
		case p == "FungusEating_Preferred":
			d.Fungal = true
		case p == "FungusEating_Despised":
			d.NoFungus = true
		}
	}
	d.FineMeals = fineMeals(pawn)
	return d, true
}

// DietFoods is the food definitions a diet allows (#1541). Everyone gets
// every meal tier and raw food as a fallback, except: human meat only for
// a cannibal; insect meat only where it is loved; a vegetarian no meat
// and no meat-only meal; a carnivore no vegetable, no meat-free meal and
// no fungus unless fungus is preferred; fungus never where it is
// despised; fine and lavish meals only in the mood tier (#1542), and never
// for an ascetic (not also a gourmand), whose mood it does not feel; the
// travel reserve (pemmican, packaged survival meals) never.
func DietFoods(d Diet, foods []Food) []string {
	allowed := func(f Food) bool {
		if slices.Contains(travelReserve, f.Def) {
			return false
		}
		switch f.Kind {
		case FoodKindHumanMeat:
			return d.Cannibal && !d.Vegetarian
		case FoodKindInsectMeat:
			return d.InsectMeat && !d.Vegetarian
		case FoodKindRawMeat:
			return !d.Vegetarian
		case FoodKindVegetable:
			return !d.Carnivore
		case FoodKindFungus:
			return !d.NoFungus && (d.Fungal || !d.Carnivore)
		case FoodKindMealFine, FoodKindMealLavish:
			if !d.FineMeals || d.Ascetic && !d.Gourmand {
				return false
			}
		}
		if d.Vegetarian && f.Ingredients == MealMeatOnly || d.Carnivore && f.Ingredients == MealNonMeat {
			return false
		}
		return true
	}
	defs := []string{}
	for _, f := range foods {
		if allowed(f) {
			defs = append(defs, f.Def)
		}
	}
	slices.Sort(defs)
	return slices.Compact(defs)
}

// DietPolicyChanges are the per-pawn food policy writes owed (#1541): each
// colonist with a food policy holds the policy labelled with its short
// name, allowing DietFoods. A pawn whose short name another owned pawn
// shares (#1310 renames it) or that several policies carry waits.
func DietPolicyChanges(pawns []WorkPawn, names []OwnedName, policies []FoodPolicyEntry, foods []Food) []FoodPolicyChange {
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	var out []FoodPolicyChange
	for _, pawn := range pawns {
		name := short[pawn.ID]
		if _, ok := pawn.FoodRestriction.Value(); !ok || name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		diet, ok := PawnDiet(pawn)
		if !ok {
			continue
		}
		want := DietFoods(diet, foods)
		var own []FoodPolicyEntry
		for _, p := range policies {
			if p.Label == name {
				own = append(own, p)
			}
		}
		if len(own) > 1 {
			continue
		}
		var change FoodPolicyChange
		if len(own) == 0 || !slices.Equal(sortedCopy(own[0].Allowed), want) {
			v, err := domain.NewFoodPolicy(name, want)
			if err != nil {
				continue
			}
			change.Write = &v
		}
		if len(own) == 0 || !slices.Contains(own[0].Pawns, pawn.ID) {
			s, err := domain.NewFoodPolicySetting(domain.PawnID(pawn.ID), name)
			if err != nil {
				continue
			}
			change.Assign = &s
		}
		if change.Write != nil || change.Assign != nil {
			out = append(out, change)
		}
	}
	return out
}
