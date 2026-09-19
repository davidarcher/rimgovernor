package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// Food completeness counts consumer and stock rows together. Repeated eater IDs
// are explicit native eligibility, never a default of "every consumer".
func ValidateFoodSupply(v *o.FoodSupplyFacts) error {
	if v == nil || len(v.Consumers) > 256 || len(v.Stocks) > 4096 {
		return contract("food supply exceeds bound")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := colonyCounts(v.Completeness, len(v.Consumers)+len(v.Stocks), 4352); err != nil {
		return err
	}
	if v.Completeness.GetFiltered() != 0 {
		return contract("filtered food census")
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
		if row == nil || row.Item == nil || validID(row.Item.GetId()) != nil || validID(row.Item.GetDefName()) != nil || stocks[row.Item.GetId()] || row.Count != nil && row.GetCount() < 0 || !combatNumber(row.Nutrition, true) || !combatNumber(row.TemperatureC, false) || row.RotTicks != nil && row.GetRotTicks() < 0 {
			return contract("invalid food stock")
		}
		stocks[row.Item.GetId()] = true
		if row.GetReserve() && (row.HolderId != nil || row.Item.GetDefName() != "Pemmican" && row.Item.GetDefName() != "MealSurvivalPack") {
			return contract("reserve must be shared pemmican or survival meals")
		}
		if row.Item.Label != nil || row.Item.MapId != nil || row.Item.Position != nil || row.Item.Snapshot != nil {
			return contract("food stock references are identity-only")
		}
		if len(row.EaterIds) == 0 || len(row.EaterIds) > len(consumers) {
			return contract("missing food eligibility")
		}
		eaters := map[string]bool{}
		for _, id := range row.EaterIds {
			if !consumers[id] || eaters[id] {
				return contract("invalid food eligibility")
			}
			eaters[id] = true
		}
		if row.HolderId != nil && (!consumers[row.GetHolderId()] || len(row.EaterIds) != 1 || row.EaterIds[0] != row.GetHolderId()) {
			return contract("private food cannot be shared")
		}
		if row.Perishable != nil && !row.GetPerishable() && row.RotTicks != nil {
			return contract("durable food has a rot deadline")
		}
	}
	return nil
}
