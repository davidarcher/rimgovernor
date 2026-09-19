package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestLarderSupplyAdmissionRequiresReviewedCorpseAndDirection(t *testing.T) {
	for _, change := range []string{"valid", "other-corpse", "moved", "forbid"} {
		t.Run(change, func(t *testing.T) {
			s := open(t, memoryPath(t))
			r := routineRequest()
			cell := domain.Cell{X: 2, Z: 3}
			stock := policy.FoodStock{ID: "corpse", DefName: "Corpse_Muffalo", Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(15.), Eaters: []policy.PawnID{"a"}, Perishable: domain.Known(true), RotTicks: domain.Known(int64(600000)), Roofed: domain.Known(true), TemperatureC: domain.Known(-5.), Room: domain.Known("freezer"), Corpse: true, Forbidden: domain.Known(true), MeatAmount: domain.Known(300.), BodySize: domain.Known(2.), TileFootprint: domain.Known(int64(1))}
			r.Facts.FoodStorageUpkeep = policy.FoodStorageStocks(policy.FoodSupply{Stocks: []policy.FoodStock{stock}})
			r.Facts.FoodStorageUpkeep.Larder = domain.Known(policy.FoodLarder{CookDemandNutrition: .5, Corpses: []policy.CorpseHandling{{ID: stock.ID, Cell: cell}}})
			review := reviewRoutine(t, s, &r)
			goal := routineGoal(t, review, policy.MaintainFoodStorage)
			id := stock.ID
			if change == "other-corpse" {
				id = "other"
			}
			if change == "moved" {
				cell.X++
			}
			supply, err := domain.NewSupplyAllow(id, string(stock.DefName), cell)
			if change == "forbid" {
				supply, err = domain.NewSupplyForbid(id, string(stock.DefName), cell)
			}
			if err != nil {
				t.Fatal(err)
			}
			action, _ := domain.NewSupplyAllowAction("release", supply)
			plan, _ := domain.NewPlan("larder", 1, []domain.Action{action})
			_, err = s.CommitGoalMethod(context.Background(), goal.Goal.ID, goal.Revision, "release", plan)
			if (err == nil) != (change == "valid") {
				t.Fatal(change, err)
			}
		})
	}
}
