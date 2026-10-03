package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FoodRestriction is the pawn's current food policy: its load id, allowed
// definitions. It is observation, never an
// explicit controller preference.
type FoodRestriction struct {
	PolicyID string
	Allowed  []string
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
	FoodKindKibble        FoodKind = "kibble"
	FoodKindHay           FoodKind = "hay"
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
	// Reserve: the colony has no other food to eat, so the travel reserve
	// (pemmican, packaged survival meals) is allowed at home too.
	Reserve bool
}

// moodTierMargin is how far above the minor break threshold a pawn's mood
// still counts as near a break (#1542).
const moodTierMargin = 0.1

// travelReserve are the foods kept for caravans: not eaten at home while
// the colony has other food (#1542); when it is short of food (Diet.Reserve)
// they are the food. The one list Go decides it by; native only reports defs
// and forbidden state.
var travelReserve = []Resource{"Pemmican", "MealSurvivalPack"}

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

// The history events each diet fact reads the ideoligion on (the game's
// HistoryEventDefOf members), for the shared precept rule (#1656).
var (
	humanMeatEvents  = []string{"AteHumanMeat", "AteHumanMeatDirect", "AteHumanMeatAsIngredient"}
	insectMeatEvents = []string{"AteInsectMeatDirect", "AteInsectMeatAsIngredient"}
	meatEvents       = []string{"AteMeat"}
	nonMeatEvents    = []string{"AteNonMeat"}
	fungusEvents     = []string{"AteFungus", "AteFungusAsIngredient"}
)

// PawnDiet reads the pawn's diet; unknown while its traits or policy
// inputs are, or, for a pawn with precepts, the ideoligion defs.
func PawnDiet(pawn WorkPawn, ideology domain.Fact[Ideoligion]) (Diet, bool) {
	traits, ok := pawn.Traits.Value()
	inputs, known := pawn.PolicyInputs.Value()
	if !ok || !known {
		return Diet{}, false
	}
	d, ok := DietOf(traits, inputs.Precepts, ideology)
	if !ok {
		return Diet{}, false
	}
	d.FineMeals = fineMeals(pawn)
	return d, true
}

// DietOf is the diet of trait defNames and of the precepts of the pawn's
// own ideoligion, read through the shared precept rule. A pawn without
// precepts has no ideoligion and so no precept diet; one with precepts is
// unknown while the ideoligion defs are. Human meat is accepted when no
// precept costs mood for it; insect meat and fungus where a precept
// approves; vegetarian and carnivore are precepts that penalise meat or
// non-meat; fungus is despised where a precept penalises it.
func DietOf(traits []PawnTrait, precepts []string, ideology domain.Fact[Ideoligion]) (Diet, bool) {
	d := traitDiet(traits)
	if len(precepts) == 0 {
		return d, true
	}
	i, ok := ideology.Value()
	if !ok {
		return Diet{}, false
	}
	held := i.HeldBy(precepts)
	if worst, _ := held.eventStances(humanMeatEvents); worst == PreceptAllowed || worst == PreceptApproved {
		d.Cannibal = true
	}
	if _, best := held.eventStances(insectMeatEvents); best == PreceptApproved {
		d.InsectMeat = true
	}
	if worst, _ := held.eventStances(meatEvents); worst.costsMood() {
		d.Vegetarian = true
	}
	if worst, _ := held.eventStances(nonMeatEvents); worst.costsMood() {
		d.Carnivore = true
	}
	worst, best := held.eventStances(fungusEvents)
	d.Fungal = best == PreceptApproved
	d.NoFungus = worst.costsMood()
	return d, true
}

// traitDiet is the diet the pawn's traits give.
func traitDiet(traits []PawnTrait) Diet {
	var d Diet
	for _, t := range traits {
		d.Cannibal = d.Cannibal || t.Effects.Cannibal
		d.Ascetic = d.Ascetic || t.Effects.Ascetic
		d.Gourmand = d.Gourmand || t.Effects.Gourmand
	}
	return d
}

// DietFoods is the food definitions a diet allows (#1541). Everyone gets
// every meal tier and raw food as a fallback, except: human meat only for
// a cannibal; insect meat only where it is loved; a vegetarian no meat
// and no meat-only meal; a carnivore no vegetable, no meat-free meal and
// no fungus unless fungus is preferred; fungus never where it is
// despised; fine and lavish meals only in the mood tier (#1542), and never
// for an ascetic (not also a gourmand), whose mood it does not feel; the
// travel reserve (pemmican, packaged survival meals) only while the colony
// is short of other food (Diet.Reserve).
func DietFoods(d Diet, foods []Food) []string {
	allowed := func(f Food) bool {
		if ReserveFoodDefinition(Resource(f.Def)) {
			return d.Reserve
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

// FoodEater is a food policy holder outside the work census (#1543): a
// prisoner with its diet, or a tame animal with the food definitions its
// race can eat.
type FoodEater struct {
	Pawn   PawnID
	Animal bool
	// Traits and Precepts are the prisoner's diet inputs (DietOf).
	Traits   []PawnTrait
	Precepts []string
	Edible   []string
}

// captiveKinds is what a prisoner or slave eats: nutrient paste and raw food.
var captiveKinds = []FoodKind{FoodKindMealAwful, FoodKindRawMeat, FoodKindHumanMeat, FoodKindInsectMeat, FoodKindVegetable, FoodKindFungus, FoodKindAnimalProduct}

// animalKinds is what a tame animal eats: kibble, hay and raw food, never a meal.
var animalKinds = []FoodKind{FoodKindKibble, FoodKindHay, FoodKindRawMeat, FoodKindHumanMeat, FoodKindInsectMeat, FoodKindVegetable, FoodKindFungus, FoodKindAnimalProduct}

// CaptiveFoods is a prisoner's or slave's foods (#1543): its diet's paste
// and raw food.
func CaptiveFoods(d Diet, foods []Food) []string {
	var kept []Food
	for _, f := range foods {
		if slices.Contains(captiveKinds, f.Kind) {
			kept = append(kept, f)
		}
	}
	return DietFoods(d, kept)
}

// AnimalFoods is a tame animal's foods (#1543): the kibble, hay and raw
// food its race can eat. Corpses are never foods; native disallows them on
// every write.
func AnimalFoods(edible []string, foods []Food) []string {
	defs := []string{}
	for _, f := range foods {
		if slices.Contains(animalKinds, f.Kind) && slices.Contains(edible, f.Def) {
			defs = append(defs, f.Def)
		}
	}
	slices.Sort(defs)
	return slices.Compact(defs)
}

// DietPolicyChanges are the per-pawn food policy writes owed (#1541,
// #1543): each owned pawn with a food policy holds the policy labelled with
// its short name, allowing DietFoods for a colonist, CaptiveFoods for a
// slave or prisoner and AnimalFoods for a tame animal. A pawn whose short
// name another owned pawn shares (#1310 renames it) or that several
// policies carry waits.
func DietPolicyChanges(ideology domain.Fact[Ideoligion], pawns []WorkPawn, eaters []FoodEater, names []OwnedName, policies []FoodPolicyEntry, foods []Food, reserveFood bool) []FoodPolicyChange {
	type owed struct {
		id   PawnID
		want []string
	}
	var rows []owed
	for _, pawn := range pawns {
		if _, ok := pawn.FoodRestriction.Value(); !ok {
			continue
		}
		diet, ok := PawnDiet(pawn, ideology)
		if !ok {
			continue
		}
		if in, _ := pawn.PolicyInputs.Value(); in.GuestStatus == "Slave" || in.GuestStatus == "Prisoner" {
			rows = append(rows, owed{pawn.ID, CaptiveFoods(diet, foods)})
		} else {
			diet.Reserve = reserveFood
			rows = append(rows, owed{pawn.ID, DietFoods(diet, foods)})
		}
	}
	for _, e := range eaters {
		if e.Animal {
			rows = append(rows, owed{e.Pawn, AnimalFoods(e.Edible, foods)})
		} else {
			diet, ok := DietOf(e.Traits, e.Precepts, ideology)
			if !ok {
				continue
			}
			rows = append(rows, owed{e.Pawn, CaptiveFoods(diet, foods)})
		}
	}
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	var out []FoodPolicyChange
	for _, row := range rows {
		name := short[row.id]
		if name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		want := row.want
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
		if len(own) == 0 || !slices.Contains(own[0].Pawns, row.id) {
			s, err := domain.NewFoodPolicySetting(domain.PawnID(row.id), name)
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
