package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func babySupply(stock ...FoodStock) FoodSupply {
	return FoodSupply{Complete: domain.Known(true),
		Consumers: []FoodConsumer{{ID: "mum", NutritionPerDay: domain.Known(2.)}, {ID: "baby", NutritionPerDay: domain.Known(.25)}},
		Stocks:    stock}
}

func babyFoodBench(bills ...ExistingProductionBill) domain.Fact[[]ProductionBench] {
	product := func(name string, babyEdible bool) ProductionProduct {
		return ProductionProduct{Name: name, Nutrition: domain.Known(.05), Edible: domain.Known(true), BabyEdible: domain.Known(babyEdible)}
	}
	return domain.Known([]ProductionBench{{ID: "stove", Token: domain.Known("tok"), Usable: domain.Known(true),
		Recipes: []ProductionRecipe{
			{Name: "MakeMeal", Available: domain.Known(true), Products: []ProductionProduct{product("MealSimple", false)}},
			{Name: "MakeBabyFood", Available: domain.Known(true), Products: []ProductionProduct{product("BabyFood", true)}},
			{Name: "MakeBabyFoodBulk", Available: domain.Known(true), Products: []ProductionProduct{product("BabyFood", true)}},
		}, Bills: bills}})
}

func TestHungryBabyGetsBabyFoodBill(t *testing.T) {
	meal := durableFood("meal", 10, "", "mum")
	review, err := ReviewBabyFeeding([]PawnID{"baby"}, 0, babySupply(meal), 4)
	if err != nil || !review.Short || review.StockNutrition != 0 || review.TargetNutrition != BabyFoodAlertNutrition {
		t.Fatal(review, err)
	}
	got, ok := SelectProductionBill(BabyFoodBill, babyFoodBench(), domain.Known[int64](2), domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{BabyFeeding: &review})
	// The bulk recipe wins; one nutrition at 0.05 each is 20 units.
	if !ok || got.Recipe != "MakeBabyFoodBulk" || got.Mode != domain.FoodTarget || got.Target != 20 || got.Bench != "stove" {
		t.Fatal(got, ok)
	}
}

func TestBabyFeedingNeedsNoBillWhenFed(t *testing.T) {
	babyFood := durableFood("jelly", 1, "", "mum", "baby")
	babyFood.DefName = "InsectJelly"
	for _, tc := range []struct {
		name          string
		breastfeeders int
		babies        []PawnID
		supply        FoodSupply
	}{
		{"breastfeeder", 1, []PawnID{"baby"}, babySupply()},
		{"stocked", 0, []PawnID{"baby"}, babySupply(babyFood)},
		{"no babies", 0, nil, babySupply()},
	} {
		t.Run(tc.name, func(t *testing.T) {
			review, err := ReviewBabyFeeding(tc.babies, tc.breastfeeders, tc.supply, 4)
			if err != nil || review.Short {
				t.Fatal(review, err)
			}
			if _, ok := SelectProductionBill(BabyFoodBill, babyFoodBench(), domain.Known[int64](2), domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{BabyFeeding: &review}); ok {
				t.Fatal("bill selected")
			}
		})
	}
}

func TestBabyFoodBillCorrectsUndersizedBillAndOtherFoodsCount(t *testing.T) {
	review, _ := ReviewBabyFeeding([]PawnID{"baby"}, 0, babySupply(), 4)
	small := ExistingProductionBill{ID: "b1", Recipe: "MakeBabyFoodBulk", Active: domain.Known(true), TargetCount: domain.Known[int32](5), Forever: domain.Known(false)}
	got, ok := SelectProductionBill(BabyFoodBill, babyFoodBench(small), domain.Known[int64](2), domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{BabyFeeding: &review})
	if !ok || got.Replace != "b1" || got.Target != 20 {
		t.Fatal(got, ok)
	}
	// Milk in stock (short of the floor) lowers the target by its nutrition.
	milk := durableFood("milk", .5, "", "mum", "baby")
	milk.DefName = "Milk"
	review, _ = ReviewBabyFeeding([]PawnID{"baby"}, 0, babySupply(milk), 4)
	got, ok = SelectProductionBill(BabyFoodBill, babyFoodBench(), domain.Known[int64](2), domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{BabyFeeding: &review})
	if !ok || got.Target != 10 {
		t.Fatal(got, ok)
	}
}

func TestBabyFeedingUnknownConsumerFails(t *testing.T) {
	s := babySupply()
	s.Consumers = s.Consumers[:1]
	if _, err := ReviewBabyFeeding([]PawnID{"baby"}, 0, s, 4); err == nil {
		t.Fatal("missing baby consumer accepted")
	}
}
