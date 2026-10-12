package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ClearRow is the game's own work of removing a thing and what it leaves
// (policy.ClearRow): a plant's harvestWork with its harvested yield, a
// building's deconstruct work (its WorkToBuild clamped to the
// JobDriver_Deconstruct limits) with the fraction of its cost the game refunds.
// ok is false for a def the catalog has no such row for.
func (catalog *DefinitionCatalog) ClearRow(def, stuff string) (row policy.ClearRow, ok bool, err error) {
	thing := catalog.ThingDef(def)
	if thing == nil {
		return policy.ClearRow{}, false, nil
	}
	if plant := thing.GetPlant(); plant != nil {
		work := float64(plant.GetHarvestWork())
		if math.IsNaN(work) || math.IsInf(work, 0) || work < 0 {
			return policy.ClearRow{}, false, nil
		}
		row.Work = work
		if yield, name := float64(plant.GetHarvestYield()), plant.GetHarvestedThingDef(); name != "" && yield > 0 {
			row.Yield = []policy.Amount{{Resource: policy.Resource(name), Count: int64(math.Round(yield))}}
		}
		return row, true, nil
	}
	work, shown, err := catalog.ShownStatValue(def, stuff, StatWorkToBuild)
	if err != nil || !shown {
		return policy.ClearRow{}, false, err
	}
	game, err := catalog.GameConstants()
	if err != nil {
		return policy.ClearRow{}, false, err
	}
	limits := game.GetJobDriver_Deconstruct()
	row.Work = math.Min(math.Max(float64(work), float64(limits.GetMinDeconstructWork())), float64(limits.GetMaxDeconstructWork()))
	costs, err := catalog.AdjustedCosts(def, stuff)
	if err != nil {
		return policy.ClearRow{}, false, err
	}
	refund := float64(thing.GetResourcesFractionWhenDeconstructed())
	for _, cost := range costs {
		if n := int64(math.Floor(float64(cost.GetUnits()) * refund)); n > 0 {
			row.Yield = append(row.Yield, policy.Amount{Resource: policy.Resource(cost.GetDefName()), Count: n})
		}
	}
	return row, true, nil
}
