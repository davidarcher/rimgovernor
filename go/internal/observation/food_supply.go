package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func DecodeFoodSupply(v *o.FoodSupplyFacts) (policy.FoodSupply, error) {
	if err := bridge.ValidateFoodSupply(v); err != nil {
		return policy.FoodSupply{}, err
	}
	supply := policy.FoodSupply{Complete: domain.Known(true)}
	for _, row := range v.Consumers {
		supply.Consumers = append(supply.Consumers, policy.FoodConsumer{ID: policy.PawnID(row.GetPawnId()), NutritionPerDay: optional(row.NutritionPerDay)})
	}
	for _, row := range v.Stocks {
		stock := policy.FoodStock{ID: row.Item.GetId(), Holder: domain.Known(policy.PawnID(row.GetHolderId())), Nutrition: optional(row.Nutrition), Perishable: optional(row.Perishable), RotTicks: optional(row.RotTicks), DefName: policy.Resource(row.Item.GetDefName())}
		if row.Count != nil {
			stock.Count = domain.Known(int64(row.GetCount()))
		}
		for _, id := range row.EaterIds {
			stock.Eaters = append(stock.Eaters, policy.PawnID(id))
		}
		supply.Stocks = append(supply.Stocks, stock)
	}
	return supply, nil
}
