package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainBabyFeeding keeps babies fed (#1681, epic #1667). The game feeds a
// baby itself: a lactating pawn breastfeeds any baby, otherwise a colonist on
// Childcare work (the mother is Urgent and every other pawn Childcare by
// default; Rimworld wiki, Baby > Food) bottle-feeds it food it can eat, which is
// baby food, milk or insect jelly (ITab_Pawn_Feeding.BabyConsumableFoods,
// IngestibleProperties.babiesCanIngest). Childcare is pinned work for every
// pawn already, so the one thing the colony owes is the food: with no
// breastfeeder and too little baby-edible stock, the goal places a standing
// target-count bill for a baby-edible recipe through the existing production
// bill write. No other write is owed; beds and play are the nursery's (#1680).
const MaintainBabyFeeding ConcernID = "MaintainBabyFeeding"

// BabyFoodBill is MaintainBabyFeeding's bill purpose.
const BabyFoodBill BillPurpose = "baby_food"

const babyFeedingPriority = 2

// BabyFoodAlertNutrition is the baby-edible nutrition per baby below which
// the game raises Alert_LowBabyFood (Alert_LowBabyFood.NutritionThresholdPerBaby,
// read from the game assembly's constants).
const BabyFoodAlertNutrition = 1.0

// BabyFeeding is the review of baby food: what the babies' foods hold against
// what they need. Short is the goal's deficit.
type BabyFeeding struct {
	Babies int
	// Breastfed: a colonist can breastfeed, which feeds every baby.
	Breastfed bool
	// StockNutrition is the shared baby-edible nutrition (stocks a baby
	// eats); ByDefinition splits it by definition.
	StockNutrition  float64
	ByDefinition    map[Resource]float64
	TargetNutrition float64
	Short           bool
}

// ReviewBabyFeeding reviews the babies' food. The target is the babies'
// native nutrition per day over targetDays, at least the game's alert
// threshold per baby. A baby with no known consumer row makes it an error.
func ReviewBabyFeeding(babies []PawnID, breastfeeders int, supply FoodSupply, targetDays float64) (BabyFeeding, error) {
	r := BabyFeeding{Babies: len(babies), Breastfed: breastfeeders > 0, ByDefinition: map[Resource]float64{}}
	if len(babies) == 0 {
		return r, nil
	}
	if !fieldPositive(targetDays) || targetDays > 60 {
		return BabyFeeding{}, ErrFoodFacts
	}
	isBaby := map[PawnID]bool{}
	for _, b := range babies {
		isBaby[b] = true
	}
	perDay, found := 0., 0
	for _, c := range supply.Consumers {
		if !isBaby[c.ID] {
			continue
		}
		rate, known := c.NutritionPerDay.Value()
		if !known || !foodNumber(rate) {
			return BabyFeeding{}, ErrFoodFacts
		}
		perDay += rate
		found++
	}
	if found != len(isBaby) {
		return BabyFeeding{}, ErrFoodFacts
	}
	for _, s := range supply.Stocks {
		holder, hk := s.Holder.Value()
		amount, nk := s.Nutrition.Value()
		if !hk || !nk || !foodNumber(amount) {
			return BabyFeeding{}, ErrFoodFacts
		}
		if holder != "" || s.Corpse {
			continue
		}
		for _, e := range s.Eaters {
			if isBaby[e] {
				r.StockNutrition += amount
				r.ByDefinition[s.DefName] += amount
				break
			}
		}
	}
	floor := float64(len(babies)) * BabyFoodAlertNutrition
	r.TargetNutrition = math.Max(perDay*targetDays, floor)
	r.Short = !r.Breastfed && r.StockNutrition < floor
	return r, nil
}

// SelectBabyFoodBill selects the standing bill that makes baby-edible food:
// a recipe whose single product a baby can eat. Native counts the product's
// stock toward the target, and other baby foods reduce it. None is selected
// unless the review is short.
func SelectBabyFoodBill(benches domain.Fact[[]ProductionBench], review BabyFeeding) (BillSelection, bool) {
	rows, known := benches.Value()
	if !known || !review.Short || !fieldPositive(review.TargetNutrition) {
		return BillSelection{}, false
	}
	return selectTargetBill(rows, targetBillSpec{
		family: func(p ProductionProduct) bool { edible, ok := p.BabyEdible.Value(); return ok && edible },
		target: func(def Resource, nutrition float64) float64 {
			return (review.TargetNutrition - review.StockNutrition + review.ByDefinition[def]) / nutrition
		},
	})
}
