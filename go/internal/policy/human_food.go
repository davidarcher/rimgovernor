package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

type HumanButcherCandidate struct {
	ID                         PawnID
	Traits                     domain.Fact[[]PawnTrait]
	PreceptAcceptable, CanWork domain.Fact[bool]
}

func QualifyingHumanButcher(rows []HumanButcherCandidate) (PawnID, bool) {
	if len(rows) > 256 {
		return "", false
	}
	var selected PawnID
	seen := map[PawnID]bool{}
	for _, row := range rows {
		if !foodID(string(row.ID)) || seen[row.ID] {
			return "", false
		}
		seen[row.ID] = true
		if HumanButcherEligible(row.Traits, row.PreceptAcceptable, row.CanWork) && (selected == "" || row.ID < selected) {
			selected = row.ID
		}
	}
	return selected, selected != ""
}

// Native candidates already establish disposition, assignment and reachability;
// execution rechecks them before the write. Never substitute another worker.
func SelectHumanButcher(benches domain.Fact[[]ProductionBench]) (BillSelection, bool) {
	rows, known := benches.Value()
	if !known || len(rows) > 256 {
		return BillSelection{}, false
	}
	var choices []BillSelection
	animalBill := map[string]bool{}
	for _, b := range rows {
		usable, uk := b.Usable.Value()
		nutrition, nk := b.HumanCorpseNutrition.Value()
		token, tk := b.Token.Value()
		if !b.Butcher || !uk || !usable || !nk || !foodNumber(nutrition) || nutrition <= 0 || !tk || !foodID(token) || !foodID(b.ID) || len(b.Bills) >= 15 || len(b.HumanButchers) > 256 {
			continue
		}
		exists := false
		for _, bill := range b.Bills {
			exists = exists || bill.Recipe == "ButcherCorpseFlesh" && bill.Humanlike
			animalBill[b.ID] = animalBill[b.ID] || bill.Recipe == "ButcherCorpseFlesh" && !bill.Humanlike
		}
		available := false
		for _, recipe := range b.Recipes {
			v, k := recipe.Available.Value()
			available = available || recipe.Name == "ButcherCorpseFlesh" && k && v
		}
		if exists || !available {
			continue
		}
		if worker, ok := QualifyingHumanButcher(b.HumanButchers); ok {
			choices = append(choices, BillSelection{Bench: b.ID, Recipe: "ButcherCorpseFlesh", Token: token, Mode: domain.HumanButcherForever, Worker: string(worker)})
		}
	}
	sort.Slice(choices, func(i, j int) bool {
		if animalBill[choices[i].Bench] != animalBill[choices[j].Bench] {
			return animalBill[choices[i].Bench]
		}
		if choices[i].Bench != choices[j].Bench {
			return choices[i].Bench < choices[j].Bench
		}
		return choices[i].Worker < choices[j].Worker
	})
	if len(choices) == 0 {
		return BillSelection{}, false
	}
	return choices[0], true
}

// HumanButcherEligible separates the worker's disposition from the colony's
// response. Other pawns may still receive native butchery memories.
func HumanButcherEligible(traits domain.Fact[[]PawnTrait], preceptAcceptable, canWork domain.Fact[bool]) bool {
	works, known := canWork.Value()
	if !known || !works {
		return false
	}
	if acceptable, known := preceptAcceptable.Value(); known && acceptable {
		return true
	}
	rows, known := traits.Value()
	if !known {
		return false
	}
	for _, trait := range rows {
		if trait.Name == "Psychopath" || trait.Name == "Bloodlust" || trait.Name == "Cannibal" {
			return true
		}
	}
	return false
}

type HumanMeatRoute string

const (
	HumanMeatFeed          HumanMeatRoute = "animal_feed"
	HumanMeatSurvivalTrade HumanMeatRoute = "survival_trade"
	HumanMeatRawTrade      HumanMeatRoute = "raw_trade"
	HumanMeatMeals         HumanMeatRoute = "eligible_meals"
)

// RouteHumanMeat allocates existing nutrition once, in destination order.
// Survival production requires observed vegetable nutrition and production
// capacity; raw trade is the fallback when nobody will eat the remainder.
type HumanMeatRouting struct {
	Nutrition, FeedShortfall, EligibleMealDemand float64
	VegetableNutrition                           float64
	CanMakeSurvivalMeals                         bool
}

type HumanMeatAllocation struct {
	Route     HumanMeatRoute
	Nutrition float64
}

func RouteHumanMeat(r HumanMeatRouting) ([]HumanMeatAllocation, error) {
	if !foodNumber(r.Nutrition) || !foodNumber(r.FeedShortfall) || !foodNumber(r.EligibleMealDemand) || !foodNumber(r.VegetableNutrition) {
		return nil, ErrFoodFacts
	}
	var out []HumanMeatAllocation
	allocate := func(route HumanMeatRoute, amount float64) {
		amount = min(amount, r.Nutrition)
		if amount > 0 {
			out = append(out, HumanMeatAllocation{route, amount})
			r.Nutrition -= amount
		}
	}
	allocate(HumanMeatFeed, r.FeedShortfall)
	// Only nutrition beyond eligible diners' demand is trade surplus.
	meal := min(r.Nutrition, r.EligibleMealDemand)
	if r.CanMakeSurvivalMeals {
		allocate(HumanMeatSurvivalTrade, min(r.VegetableNutrition, r.Nutrition-meal))
	}
	allocate(HumanMeatRawTrade, r.Nutrition-meal)
	allocate(HumanMeatMeals, meal)
	return out, nil
}
