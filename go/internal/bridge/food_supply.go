package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// Food completeness counts consumer and stock rows together. Repeated eater IDs
// are explicit native eligibility, never a default of "every consumer".
// A stock references its things table row (#1343); JoinFoodSupply checks
// the facts that need the row.
func ValidateFoodSupply(v *o.FoodSupplyFacts) error {
	if v == nil {
		return contract("food supply exceeds bound")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	consumers := map[string]bool{}
	for _, row := range v.Consumers {
		if row == nil || validID(row.GetPawnId()) != nil || consumers[row.GetPawnId()] || !combatNumber(row.NutritionPerDay, true) {
			return contract("invalid food consumer")
		}
		consumers[row.GetPawnId()] = true
	}
	stocks := map[string]bool{}
	for _, row := range v.Stocks {
		if row == nil || !uniqueRef(row.Item, stocks) || !combatNumber(row.Nutrition, true) {
			return contract("invalid food stock")
		}
		if len(row.Eaters) > len(consumers) {
			return contract("missing food eligibility")
		}
		eaters := map[string]bool{}
		for _, id := range RefIDs(row.Eaters) {
			if !consumers[id] || eaters[id] {
				return contract("invalid food eligibility")
			}
			eaters[id] = true
		}
		if !optionalRef(row.Holder) || row.Holder != nil && (!consumers[row.GetHolder().GetId()] || len(row.Eaters) != 1 || row.Eaters[0].GetId() != row.GetHolder().GetId()) {
			return contract("private food cannot be shared")
		}
	}
	if larder := v.Larder; larder != nil {
		if !combatNumber(&larder.RawMeatNutrition, true) || !combatNumber(&larder.CookDemandNutrition, true) {
			return contract("invalid food larder")
		}
		seen := map[string]bool{}
		for _, row := range larder.Corpses {
			if row == nil || !stocks[row.StockId] || seen[row.StockId] || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0 || row.HaulerId != nil && validID(row.GetHaulerId()) != nil || row.FrozenDestination && row.GetHaulerId() == "" {
				return contract("invalid corpse handling")
			}
			seen[row.StockId] = true
		}
		for _, cell := range larder.ColdSites {
			if cell == nil || cell.X == nil || cell.Z == nil || cell.GetX() < 0 || cell.GetZ() < 0 {
				return contract("invalid cold site")
			}
		}
	}
	return nil
}

// JoinFoodSupply resolves v's stocks against things, a frame's things
// table, and checks the facts that need a stock's row: eligibility for
// any food but a corpse or human meat and that
// a larder corpse is a corpse with meat. It returns false, with nothing
// checked, when the table misses a stock: the supply waits for a later
// frame.
func JoinFoodSupply(v *o.FoodSupplyFacts, things Things) (map[string]*o.Thing, bool, error) {
	if err := ValidateFoodSupply(v); err != nil {
		return nil, false, err
	}
	rows := make(map[string]*o.Thing, len(v.Stocks))
	for _, stock := range v.Stocks {
		row, ok := things.Row(stock.Item)
		if !ok {
			return nil, false, nil
		}
		rows[stock.Item.GetId()] = row
	}
	for _, stock := range v.Stocks {
		row := rows[stock.Item.GetId()]
		def := row.GetThing().GetDefName()
		if validID(def) != nil {
			return nil, false, contract("food stock row without a definition")
		}
		if len(stock.Eaters) == 0 && !row.GetCorpse() && !row.GetIsHumanMeat() {
			return nil, false, contract("missing food eligibility")
		}
		if row.GetCorpse() && row.GetMeatAmount() <= 0 {
			return nil, false, contract("corpse stock without meat")
		}
	}
	for _, corpse := range v.GetLarder().GetCorpses() {
		if !rows[corpse.StockId].GetCorpse() {
			return nil, false, contract("larder corpse is no corpse stock")
		}
	}
	return rows, true, nil
}
