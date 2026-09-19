package policy

import (
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type MealTier string

const (
	MealSimple MealTier = "simple"
	MealFine   MealTier = "fine"
	MealLavish MealTier = "lavish"
	MealPaste  MealTier = "paste"
)

type MealCook struct {
	Pawn  PawnID
	Skill int32
}

// MealTierRequest is the food-plan owner's tick-consistent input. RawRunwayDays
// excludes prepared meals and reserves. Cooks contains only allocated, available
// cooking workers; a skilled but unallocated pawn is not cooking capacity.
type MealTierRequest struct {
	Plan                 domain.Fact[FoodPlan]
	RawRunwayDays        domain.Fact[float64]
	MinDays, TargetDays  float64
	Cooks                domain.Fact[[]MealCook]
	CookLaborConstrained domain.Fact[bool]
	HighExpectations     domain.Fact[bool]
	Previous             MealTier
	Paste                domain.Fact[Infrastructure]
	Environment          domain.Fact[ControlledEnvironment]
}

type MealRecipeChoice struct {
	Bench, Recipe                              string
	Mood, NutrientEfficiency, WorkPerNutrition float64
}

// PasteNetwork is a power-feasible construction recommendation, never proof of
// connectivity, hopper stock or native placement. Recipes contains only the
// chosen tier, including existing bills so callers can reconcile owned work.
type MealTierReview struct {
	Tier         MealTier
	Recipes      []MealRecipeChoice
	PasteNetwork string
	Reason       string
	Terms        []FoodPlanTerm
}

var ErrMealTierFacts = errors.New("meal tier inputs unavailable or invalid")

// ReviewMealTier upgrades half a day above the target (or half the min/target
// gap when narrower), and retains an advanced tier until runway drops below
// target. Lavish additionally requires observed expectations pressure and strict
// surplus. Losing a cook or an ingredient source overrides the runway latch.
func ReviewMealTier(r MealTierRequest, benches domain.Fact[[]ProductionBench]) (MealTierReview, error) {
	plan, pk := r.Plan.Value()
	days, dk := r.RawRunwayDays.Value()
	if !pk || !dk || !foodNumber(days) || !foodNumber(r.MinDays) || !fieldPositive(r.TargetDays) || r.TargetDays > 60 || r.MinDays >= r.TargetDays || !validMealTier(r.Previous) {
		return MealTierReview{}, ErrMealTierFacts
	}
	sources, protein, ok := mealIngredientSources(plan)
	if !ok {
		return MealTierReview{}, ErrMealTierFacts
	}
	cooks, ck := r.Cooks.Value()
	if len(cooks) > 256 {
		return MealTierReview{}, ErrMealTierFacts
	}
	skill := int32(-1)
	seen := map[PawnID]bool{}
	for _, cook := range cooks {
		if !foodID(string(cook.Pawn)) || seen[cook.Pawn] || cook.Skill < 0 || cook.Skill > 20 {
			return MealTierReview{}, ErrMealTierFacts
		}
		seen[cook.Pawn] = true
		if cook.Skill > skill {
			skill = cook.Skill
		}
	}
	margin := math.Min(0.5, (r.TargetDays-r.MinDays)/2)
	review := MealTierReview{Tier: MealSimple, Reason: "raw surplus or qualified recipe unavailable", Terms: []FoodPlanTerm{{"raw_runway_days", days}, {"upgrade_days", r.TargetDays + margin}, {"downgrade_days", r.TargetDays}}}
	constrained := positive(r.CookLaborConstrained) || ck && len(cooks) == 0
	pasteNeeded := constrained || days < r.MinDays || r.Previous == MealPaste && days < r.MinDays+margin
	if pasteNeeded && len(sources) > 0 {
		if network, watts, known := mealPasteNetwork(r); known {
			review.Tier, review.PasteNetwork, review.Reason = MealPaste, network, "raw shortage or cooking labor constraint"
			review.Terms = append(review.Terms, FoodPlanTerm{"paste_power_w", watts})
			return review, nil
		}
	}
	rows, bk := benches.Value()
	if !bk || !ck {
		return MealTierReview{}, ErrMealTierFacts
	}
	if len(rows) > 256 {
		return MealTierReview{}, ErrMealTierFacts
	}
	advanced := days >= r.TargetDays+margin || (r.Previous == MealFine || r.Previous == MealLavish) && days >= r.TargetDays
	maxTier := MealSimple
	if advanced && protein {
		maxTier = MealFine
		lavishSurplus := days >= r.TargetDays+margin || r.Previous == MealLavish && days > r.TargetDays
		if positive(r.HighExpectations) && lavishSurplus {
			maxTier = MealLavish
		}
	}
	seenBenches := map[string]bool{}
	best := 0
	for _, bench := range rows {
		if !foodID(bench.ID) || seenBenches[bench.ID] || len(bench.Recipes) > 256 {
			return MealTierReview{}, ErrMealTierFacts
		}
		seenBenches[bench.ID] = true
		if bench.Butcher || !positive(bench.Usable) {
			continue
		}
		seenRecipes := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if !foodID(recipe.Name) || seenRecipes[recipe.Name] {
				return MealTierReview{}, ErrMealTierFacts
			}
			seenRecipes[recipe.Name] = true
			choice, tier, valid := mealRecipeChoice(bench.ID, recipe, skill, sources)
			if !valid || mealTierRank(tier) > mealTierRank(maxTier) {
				continue
			}
			rank := mealTierRank(tier)
			if rank < best {
				continue
			}
			if rank > best {
				review.Recipes = nil
				review.Tier = tier
				best = rank
			}
			review.Recipes = append(review.Recipes, choice)
		}
	}
	sort.Slice(review.Recipes, func(i, j int) bool {
		a, b := review.Recipes[i], review.Recipes[j]
		if a.NutrientEfficiency != b.NutrientEfficiency {
			return a.NutrientEfficiency > b.NutrientEfficiency
		}
		if a.WorkPerNutrition != b.WorkPerNutrition {
			return a.WorkPerNutrition < b.WorkPerNutrition
		}
		if a.Mood != b.Mood {
			return a.Mood > b.Mood
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		return a.Bench < b.Bench
	})
	if len(review.Recipes) > 0 {
		review.Reason = "qualified cook and retained ingredient sources"
	}
	return review, nil
}

func validMealTier(t MealTier) bool {
	return t == "" || t == MealSimple || t == MealFine || t == MealLavish || t == MealPaste
}
func mealTierRank(t MealTier) int {
	switch t {
	case MealSimple:
		return 1
	case MealFine:
		return 2
	case MealLavish:
		return 3
	}
	return 0
}

// A proposed Open channel is not yet an ingredient source. Hold preserves an
// observed-open source; Close and Unknown never satisfy a recipe slot.
func mealIngredientSources(p FoodPlan) (map[FoodIngredientClass]bool, bool, bool) {
	sources := map[FoodIngredientClass]bool{}
	if len(p.Portfolio) > 4096 || !fieldPositive(p.DemandPerDay) || !foodNumber(p.DeliveredPerDay) || math.IsNaN(p.GapPerDay) || math.IsInf(p.GapPerDay, 0) {
		return nil, false, false
	}
	seen := map[string]bool{}
	protein := false
	for _, e := range p.Portfolio {
		c := e.Channel
		key := string(c.Kind) + "/" + c.ID
		if !validFoodChannelKind(c.Kind) || !foodID(c.ID) || seen[key] || !foodNumber(e.DeliveredPerDay) || (e.Decision != FoodPlanOpen && e.Decision != FoodPlanHold && e.Decision != FoodPlanClose) {
			return nil, false, false
		}
		seen[key] = true
		if e.Decision == FoodPlanClose || !positive(c.Open) || e.DeliveredPerDay <= 0 {
			continue
		}
		switch c.Kind {
		case FoodForage, FoodCrop:
			sources[IngredientVegetable] = true
		case FoodHunt, FoodCorpse:
			sources[IngredientMeat] = true
			protein = true
		case FoodAnimalProduct:
			sources[IngredientAnimalProduct] = true
			protein = true
		case FoodFishing:
			sources[IngredientMeat] = true
		}
	}
	return sources, protein, true
}

func mealRecipeChoice(bench string, r ProductionRecipe, skill int32, sources map[FoodIngredientClass]bool) (MealRecipeChoice, MealTier, bool) {
	choice := MealRecipeChoice{Bench: bench, Recipe: r.Name}
	floor, fk := r.CookSkillFloor.Value()
	mood, mk := r.Mood.Value()
	efficiency, ek := r.NutrientEfficiency.Value()
	work, wk := r.WorkPerNutrition.Value()
	_, powerKnown := r.NeedsPower.Value()
	classes, ik := r.IngredientClasses.Value()
	if !positive(r.Available) || !fk || floor < 0 || floor > 20 || skill < floor || !mk || !foodNumber(mood) || !ek || !fieldPositive(efficiency) || !wk || !foodNumber(work) || !powerKnown || !ik || !mealSlotsSupported(classes, sources) || len(r.Products) != 1 {
		return choice, "", false
	}
	product := r.Products[0]
	nutrition, nk := product.Nutrition.Value()
	if !positive(product.Edible) || !nk || !fieldPositive(nutrition) {
		return choice, "", false
	}
	tier := MealSimple
	if mood >= 12 {
		tier = MealLavish
	} else if mood >= 5 {
		tier = MealFine
	}
	choice.Mood, choice.NutrientEfficiency, choice.WorkPerNutrition = mood, efficiency, work
	return choice, tier, true
}

func mealSlotsSupported(slots []FoodIngredientSlot, sources map[FoodIngredientClass]bool) bool {
	if len(slots) == 0 || len(slots) > 64 {
		return false
	}
	for _, slot := range slots {
		if len(slot.Alternatives) == 0 || len(slot.Alternatives) > 3 {
			return false
		}
		seen, supported := map[FoodIngredientClass]bool{}, false
		for _, kind := range slot.Alternatives {
			if seen[kind] {
				return false
			}
			seen[kind] = true
			switch kind {
			case IngredientAny:
				if len(slot.Alternatives) != 1 {
					return false
				}
				supported = len(sources) > 0
			case IngredientMeat, IngredientVegetable, IngredientAnimalProduct:
				supported = supported || sources[kind]
			default:
				return false
			}
		}
		if !supported {
			return false
		}
	}
	return true
}

func mealPasteNetwork(r MealTierRequest) (string, float64, bool) {
	infra, ik := r.Paste.Value()
	env, ek := r.Environment.Value()
	watts, wk := infra.PowerW.Value()
	if !ik || !ek || !wk || !foodID(infra.Name) || !positive(infra.Available) || !fieldPositive(watts) || len(env.Networks) > 256 {
		return "", 0, false
	}
	best, headroom := "", 0.0
	seen := map[string]bool{}
	for _, network := range env.Networks {
		if !foodID(network.ID) || seen[network.ID] {
			return "", 0, false
		}
		seen[network.ID] = true
		margin, known := network.CalmNightHeadroomW().Value()
		if !known || !positive(network.ActiveSource) || margin < watts {
			continue
		}
		if best == "" || margin > headroom || margin == headroom && network.ID < best {
			best, headroom = network.ID, margin
		}
	}
	return best, watts, best != ""
}

func (r MealTierReview) Explain() string {
	s := fmt.Sprintf("meal tier=%s reason=%s", r.Tier, r.Reason)
	for _, term := range r.Terms {
		s += fmt.Sprintf(" %s=%.3f", term.Name, term.Value)
	}
	if r.PasteNetwork != "" {
		s += " paste_network=" + r.PasteNetwork
	}
	return s
}

func selectMealBill(benches domain.Fact[[]ProductionBench], colonists domain.Fact[int64], request MealTierRequest) (BillSelection, bool) {
	review, err := ReviewMealTier(request, benches)
	if err != nil || review.Tier == MealPaste || len(review.Recipes) == 0 {
		return BillSelection{}, false
	}
	rows, _ := benches.Value()
	type key struct{ bench, recipe string }
	byBench := map[string]ProductionBench{}
	recipes := map[key]ProductionRecipe{}
	existing := map[key]bool{}
	for _, bench := range rows {
		if len(bench.Bills) > 15 {
			return BillSelection{}, false
		}
		byBench[bench.ID] = bench
		for _, recipe := range bench.Recipes {
			recipes[key{bench.ID, recipe.Name}] = recipe
		}
		for _, bill := range bench.Bills {
			existing[key{bench.ID, bill.Recipe}] = true
		}
	}
	// Preserve a matching player's bill, including suspended or filtered bills.
	// Reconciliation must prove ownership before retiring an older-tier bill.
	for _, choice := range review.Recipes {
		if existing[key{choice.Bench, choice.Recipe}] {
			return BillSelection{}, false
		}
	}
	for _, choice := range review.Recipes {
		bench := byBench[choice.Bench]
		bench.Recipes = []ProductionRecipe{recipes[key{choice.Bench, choice.Recipe}]}
		if selected, ok := SelectProductionBill(CookFood, domain.Known([]ProductionBench{bench}), colonists, request.RawRunwayDays, domain.Unknown[float64](), request.TargetDays); ok {
			return selected, true
		}
	}
	return BillSelection{}, false
}
