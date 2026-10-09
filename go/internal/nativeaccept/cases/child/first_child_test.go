package child

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The planner bills the stove's bulk baby food recipe over the single-item one
// (policy.selectTargetBill), so the case must accept either.
func TestBabyFoodBillAcceptsAnyBabyEdibleRecipe(t *testing.T) {
	recipes := []string{"Make_BabyFood", "Make_BabyFoodBulk"}
	for _, recipe := range []string{"Make_BabyFood", "Make_BabyFoodBulk"} {
		bill, err := domain.NewProductionBill("Thing_FueledStove1", recipe, domain.FoodTarget, 64)
		if err != nil {
			t.Fatal(err)
		}
		if !babyFoodBill(bill, "Thing_FueledStove1", recipes) {
			t.Errorf("%s bill not accepted", recipe)
		}
		if babyFoodBill(bill, "Thing_FueledStove2", recipes) {
			t.Errorf("%s bill on another bench accepted", recipe)
		}
	}
	other, err := domain.NewProductionBill("Thing_FueledStove1", "CookMealSimple", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	if babyFoodBill(other, "Thing_FueledStove1", recipes) {
		t.Error("a non-baby-food recipe was accepted")
	}
}
