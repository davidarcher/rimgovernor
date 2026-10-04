package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type HumanButcherCandidate struct {
	ID                         PawnID
	Traits                     domain.Fact[[]PawnTrait]
	PreceptAcceptable, CanWork domain.Fact[bool]
}

// ButcheredHumanEvent is the HistoryEventDef a butchered human raises
// (HistoryEventDefOf.ButcheredHuman); the precept rule answers by it.
const ButcheredHumanEvent = "ButcheredHuman"

// QualifyingHumanButcher picks the lowest-id eligible worker whose
// ideoligion does not forbid butchering humans (#1657). An unread ideoligion
// with Ideology installed holds (#1922); without the expansion nothing is
// forbidden and the native disposition still gates the worker.
func QualifyingHumanButcher(rows []HumanButcherCandidate, ideology IdeologyRead) (PawnID, bool) {
	var selected PawnID
	seen := map[PawnID]bool{}
	for _, row := range rows {
		if !foodID(string(row.ID)) || seen[row.ID] {
			return "", false
		}
		seen[row.ID] = true
		if HumanButcherEligible(row.Traits, row.PreceptAcceptable, row.CanWork) && !butcherHeld(ideology, row.Traits) && (selected == "" || row.ID < selected) {
			selected = row.ID
		}
	}
	return selected, selected != ""
}

// Native candidates already establish disposition, assignment and reachability;
// execution rechecks them before the write. Never substitute another worker.
func SelectHumanButcher(benches domain.Fact[[]ProductionBench], ideology IdeologyRead) (BillSelection, bool) {
	rows, known := benches.Value()
	if !known {
		return BillSelection{}, false
	}
	var choices []BillSelection
	animalBill := map[string]bool{}
	for _, b := range rows {
		usable, uk := b.Usable.Value()
		nutrition, nk := b.HumanCorpseNutrition.Value()
		token, tk := b.Token.Value()
		if !b.Butcher || !uk || !usable || !nk || !foodNumber(nutrition) || nutrition <= 0 || !tk || !foodID(token) || !foodID(b.ID) || len(b.Bills) >= 15 {
			continue
		}
		exists := false
		for _, bill := range b.Bills {
			exists = exists || bill.Role == domain.RoleButcherFlesh && bill.Humanlike
			animalBill[b.ID] = animalBill[b.ID] || bill.Role == domain.RoleButcherFlesh && !bill.Humanlike
		}
		butcher := ""
		for _, recipe := range b.Recipes {
			v, k := recipe.Available.Value()
			if recipe.Role == domain.RoleButcherFlesh && k && v {
				butcher = recipe.Name
			}
		}
		if exists || butcher == "" {
			continue
		}
		if worker, ok := QualifyingHumanButcher(b.HumanButchers, ideology); ok {
			choices = append(choices, BillSelection{Bench: b.ID, Recipe: butcher, Token: token, Mode: domain.HumanButcherForever, Worker: string(worker)})
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

// butcherHeld is whether the precept rule forbids the worker to butcher a
// human, or cannot say (unread ideoligion, Ideology installed). Traits cancel
// an unwilling effect; unknown traits cancel none.
func butcherHeld(ideology IdeologyRead, traits domain.Fact[[]PawnTrait]) bool {
	subject := PreceptSubject{Pawn: true}
	if rows, ok := traits.Value(); ok {
		for _, t := range rows {
			subject.Traits = append(subject.Traits, t.Name)
		}
	}
	stance := ideology.ActionStance(PreceptAction{HistoryEvent: ButcheredHumanEvent}, subject).Stance
	return stance == PreceptForbidden || stance == PreceptUnknown
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
		if trait.Effects.HumanButcher {
			return true
		}
	}
	return false
}

// StrangerRoute is where a stranger corpse (raider, prisoner, visitor) goes
// (#1811).
type StrangerRoute string

const (
	StrangerButcher    StrangerRoute = "butcher"
	StrangerIncinerate StrangerRoute = "incinerate"
)

// RouteStranger butchers a corpse that is still fresh while butchery is
// open and burns every other in the incinerator (#1822): a rotting or desiccated
// one is never hauled to the butchery. An unread rot stage is not spoiled.
func RouteStranger(rot domain.RotStage, butcheryOpen bool) StrangerRoute {
	if butcheryOpen && !rot.Spoiled() {
		return StrangerButcher
	}
	return StrangerIncinerate
}

// HumanButcheryOpen is whether the existing human-butcher gate would take a
// stranger corpse now: a usable butchery whose recipe is available, a worker
// who qualifies (QualifyingHumanButcher) and room for the corpse (stocked
// storage, or cells to zone). It ignores whether the human bill stands.
func HumanButcheryOpen(benches domain.Fact[[]ProductionBench], ideology IdeologyRead) bool {
	rows, known := benches.Value()
	if !known {
		return false
	}
	for _, b := range rows {
		usable, uk := b.Usable.Value()
		nutrition, nk := b.HumanCorpseNutrition.Value()
		ready, _ := b.HumanStorageReady.Value()
		if !b.Butcher || !uk || !usable || !nk || !foodNumber(nutrition) || nutrition <= 0 || !ready && len(b.HumanStorageCells) == 0 {
			continue
		}
		available := false
		for _, recipe := range b.Recipes {
			v, k := recipe.Available.Value()
			available = available || recipe.Role == domain.RoleButcherFlesh && k && v
		}
		if _, ok := QualifyingHumanButcher(b.HumanButchers, ideology); ok && available {
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
