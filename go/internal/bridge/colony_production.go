package bridge

import (
	"math"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyProduction(v *o.ColonyFactsSnapshot) error {
	if len(v.Farms) > 256 || len(v.Cooking) > 256 {
		return contract("production census exceeds bound")
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "farms" && len(v.Farms) != 0 || issue.GetField() == "cooking" && len(v.Cooking) != 0 {
			return contract("unavailable production census contains rows")
		}
	}
	farms := map[string]bool{}
	for _, farm := range v.Farms {
		if farm == nil || validID(farm.GetZoneId()) != nil || validID(farm.GetCrop()) != nil || farms[farm.GetZoneId()] {
			return contract("invalid or duplicate farm")
		}
		farms[farm.GetZoneId()] = true
		for _, count := range []*uint32{farm.UsableCells, farm.PlantedCells, farm.GrowingCells} {
			if count != nil && uint64(*count) > uint64(v.MapSize.GetWidth())*uint64(v.MapSize.GetHeight()) {
				return contract("farm cells exceed map")
			}
		}
		if farm.GrowingCells != nil && farm.PlantedCells != nil && farm.GetGrowingCells() > farm.GetPlantedCells() {
			return contract("growing farm exceeds planted cells")
		}
		for _, value := range []*float64{farm.HarvestLowerBoundDays, farm.NutritionPerHarvestCell} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0) {
				return contract("invalid farm production measure")
			}
		}
	}
	benches := map[string]bool{}
	definition := func(d *o.DefinitionRef) bool { return d != nil && validID(d.GetDefName()) == nil && d.Label == nil }
	for _, bench := range v.Cooking {
		if bench == nil || bench.Bench == nil || validID(bench.Bench.GetId()) != nil || validID(bench.Bench.GetDefName()) != nil || bench.Bench.MapId == nil || bench.Bench.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(bench.Bench.Position, v.MapSize) || benches[bench.Bench.GetId()] || bench.Bench.Label != nil || bench.Bench.Snapshot != nil {
			return contract("invalid cooking bench")
		}
		benches[bench.Bench.GetId()] = true
		if len(bench.Recipes) > 256 || len(bench.Bills) > 256 || len(bench.Production) != 0 {
			return contract("unsupported cooking details or excessive census")
		}
		recipes := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if recipe == nil || !definition(recipe.Recipe) || recipes[recipe.Recipe.GetDefName()] || !proto.Equal(recipe, &o.RecipeState{Recipe: recipe.Recipe}) {
				return contract("invalid cooking recipe")
			}
			recipes[recipe.Recipe.GetDefName()] = true
		}
		for _, bill := range bench.Bills {
			if bill == nil || !definition(bill.Recipe) || !proto.Equal(bill, &o.BillState{Recipe: bill.Recipe, Suspended: bill.Suspended}) {
				return contract("invalid cooking bill")
			}
		}
	}
	return nil
}
