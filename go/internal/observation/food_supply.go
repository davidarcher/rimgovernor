package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// DecodeFoodSupply joins v's stocks to their things table rows (#1343) and
// to their def rows in the definition catalog (#1733); known is false when
// the table misses a stock or no catalog is held, and the supply is then
// unknown until a later frame. A stock whose def the catalog lacks is a
// contract error.
func DecodeFoodSupply(v *o.FoodSupplyFacts, things bridge.Things, catalog *bridge.DefinitionCatalog) (supply policy.FoodSupply, known bool, err error) {
	rows, known, err := bridge.JoinFoodSupply(v, things)
	if err != nil || !known || catalog == nil {
		return policy.FoodSupply{}, false, err
	}
	supply, err = decodeFoodSupply(v, rows, catalog)
	if err != nil {
		return policy.FoodSupply{}, false, err
	}
	return supply, true, nil
}

// foodDef is what a food stock's def says about it (#1733): its raw
// ingredient class (a raw meat is the meat class), whether its food type
// includes vegetable or fruit, and for a corpse whether its race is humanlike.
type foodDef struct {
	class     policy.FoodIngredientClass
	rawMeat   bool
	vegetable bool
	humanlike bool
}

func foodDefFacts(catalog *bridge.DefinitionCatalog, def string, corpse bool) (foodDef, error) {
	var facts foodDef
	var err error
	if facts.class, err = catalog.RawFoodClass(def); err != nil {
		return facts, err
	}
	if facts.rawMeat, err = catalog.RawMeat(def); err != nil {
		return facts, err
	}
	if facts.vegetable, err = catalog.Vegetable(def); err != nil {
		return facts, err
	}
	if corpse {
		facts.humanlike, err = catalog.HumanlikeCorpse(def)
	}
	return facts, err
}

func decodeFoodSupply(v *o.FoodSupplyFacts, rows map[string]*o.Thing, catalog *bridge.DefinitionCatalog) (policy.FoodSupply, error) {
	supply := policy.FoodSupply{Complete: domain.Known(true)}
	if v.Larder != nil {
		larder := policy.FoodLarder{RawMeatNutrition: v.Larder.RawMeatNutrition, CookDemandNutrition: v.Larder.CookDemandNutrition}
		for _, row := range v.Larder.Corpses {
			inStorage := domain.Unknown[bool]()
			if row.InStorage != nil {
				inStorage = domain.Known(row.GetInStorage())
			}
			larder.Corpses = append(larder.Corpses, policy.CorpseHandling{InStorage: inStorage, ID: row.StockId, Cell: domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}, Hauler: domain.PawnID(row.GetHaulerId()), FrozenDestination: row.FrozenDestination})
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
		def := row.GetThing().GetDefName()
		defFacts, err := foodDefFacts(catalog, def, row.GetCorpse())
		if err != nil {
			return policy.FoodSupply{}, err
		}
		// A thing rots exactly while it has a rot deadline.
		stock := policy.FoodStock{ID: s.Item.GetId(), IsHumanMeat: row.GetIsHumanMeat(), RawMeat: defFacts.rawMeat, IsHumanlike: defFacts.humanlike, Vegetable: defFacts.vegetable, RawClass: domain.Known(defFacts.class), Holder: domain.Known(policy.PawnID(s.GetHolder().GetId())), Nutrition: optional(s.Nutrition), Perishable: domain.Known(row.RotTicks != nil), RotTicks: optional(row.RotTicks), DefName: policy.Resource(def), Roofed: optional(row.Roofed), TemperatureC: optional(row.TemperatureC), Room: optionalRef(row.Room)}
		if row.StackCount != nil {
			stock.Count = domain.Known(row.GetStackCount())
		}
		stock.Corpse = row.GetCorpse()
		// A forbidden stack is the travel reserve when its def is one; any
		// other forbidden stack has no eaters and counts only as human meat.
		barred := false
		if row.GetForbidden() && !stock.Corpse {
			stock.Reserve = policy.ReserveFoodDefinition(stock.DefName)
			barred = !stock.Reserve
		}
		stock.Forbidden = optional(row.Forbidden)
		stock.MeatAmount = optional(row.MeatAmount)
		stock.BodySize = optional(row.BodySize)
		stock.TileFootprint = optional(row.TileFootprint)
		if barred && !stock.IsHumanMeat {
			// Census-only row (Barred): no eaters, outside the forecast.
			supply.Barred = append(supply.Barred, stock)
			continue
		}
		for _, id := range bridge.RefIDs(s.Eaters) {
			if barred {
				break
			}
			stock.Eaters = append(stock.Eaters, policy.PawnID(id))
		}
		supply.Stocks = append(supply.Stocks, stock)
	}
	return supply, nil
}
