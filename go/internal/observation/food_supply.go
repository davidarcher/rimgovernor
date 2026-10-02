package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DecodeFoodSupply joins v's stocks to their things table rows (#1343);
// known is false when the table misses a stock, and the supply is then
// unknown until a later frame.
func DecodeFoodSupply(v *o.FoodSupplyFacts, things bridge.Things) (supply policy.FoodSupply, known bool, err error) {
	rows, known, err := bridge.JoinFoodSupply(v, things)
	if err != nil || !known {
		return policy.FoodSupply{}, false, err
	}
	supply = decodeFoodSupply(v, rows)
	return supply, true, nil
}

func decodeFoodSupply(v *o.FoodSupplyFacts, rows map[string]*o.Thing) policy.FoodSupply {
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
		supply.Consumers = append(supply.Consumers, policy.FoodConsumer{ID: policy.PawnID(row.GetPawnId()), NutritionPerDay: optional(row.NutritionPerDay), HumanMeatAcceptable: optional(row.HumanMeatAcceptable)})
	}
	for _, s := range v.Stocks {
		row := rows[s.Item.GetId()]
		stock := policy.FoodStock{ID: s.Item.GetId(), IsHumanMeat: row.GetIsHumanMeat(), RawMeat: row.GetRawMeat(), IsHumanlike: row.GetIsHumanlike(), Vegetable: row.GetVegetable(), Reserve: s.GetReserve(), Holder: domain.Known(policy.PawnID(s.GetHolderId())), Nutrition: optional(s.Nutrition), Perishable: optional(row.Perishable), RotTicks: optional(row.RotTicks), DefName: policy.Resource(row.GetThing().GetDefName()), Roofed: optional(row.Roofed), TemperatureC: optional(row.TemperatureC), Room: optional(row.RoomId)}
		if row.StackCount != nil {
			stock.Count = domain.Known(row.GetStackCount())
		}
		stock.Corpse = row.GetCorpse()
		stock.RawClass = rawFoodClass(row.RawClass)
		if stock.Corpse {
			stock.Forbidden = optional(row.Forbidden)
		}
		stock.MeatAmount = optional(row.MeatAmount)
		stock.BodySize = optional(row.BodySize)
		stock.TileFootprint = optional(row.TileFootprint)
		for _, id := range s.EaterIds {
			stock.Eaters = append(stock.Eaters, policy.PawnID(id))
		}
		supply.Stocks = append(supply.Stocks, stock)
	}
	return supply
}
