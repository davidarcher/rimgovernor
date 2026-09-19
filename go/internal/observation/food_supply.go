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
	if v.Larder != nil {
		larder := policy.FoodLarder{RawMeatNutrition: v.Larder.RawMeatNutrition, CookDemandNutrition: v.Larder.CookDemandNutrition}
		for _, row := range v.Larder.Corpses {
			larder.Corpses = append(larder.Corpses, policy.CorpseHandling{ID: row.StockId, Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Hauler: domain.PawnID(row.GetHaulerId()), FrozenDestination: row.FrozenDestination})
		}
		for _, cell := range v.Larder.ColdSites {
			larder.ColdSites = append(larder.ColdSites, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		supply.Larder = domain.Known(larder)
	}
	for _, row := range v.Consumers {
		supply.Consumers = append(supply.Consumers, policy.FoodConsumer{ID: policy.PawnID(row.GetPawnId()), NutritionPerDay: optional(row.NutritionPerDay)})
	}
	for _, row := range v.Stocks {
		stock := policy.FoodStock{ID: row.Item.GetId(), Reserve: row.GetReserve(), Holder: domain.Known(policy.PawnID(row.GetHolderId())), Nutrition: optional(row.Nutrition), Perishable: optional(row.Perishable), RotTicks: optional(row.RotTicks), DefName: policy.Resource(row.Item.GetDefName()), Roofed: optional(row.Roofed), TemperatureC: optional(row.TemperatureC), Room: optional(row.RoomId)}
		if row.Count != nil {
			stock.Count = domain.Known(int64(row.GetCount()))
		}
		stock.Corpse = row.GetCorpse()
		stock.RawClass = rawFoodClass(row.RawClass)
		stock.Forbidden = optional(row.Forbidden)
		stock.MeatAmount = optional(row.MeatAmount)
		stock.BodySize = optional(row.BodySize)
		stock.TileFootprint = optional(row.TileFootprint)
		for _, id := range row.EaterIds {
			stock.Eaters = append(stock.Eaters, policy.PawnID(id))
		}
		supply.Stocks = append(supply.Stocks, stock)
	}
	return supply, nil
}
