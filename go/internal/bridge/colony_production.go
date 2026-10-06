package bridge

import (
	"math"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// validFarmMeasures checks a farm's raw growth facts: counts within the
// planted cells, growth a fraction, factors and nutrition non-negative, every
// number finite (temperatures may be negative).
func validFarmMeasures(farm *o.FarmFacts) bool {
	if farm.PlantedCells != nil {
		for _, count := range []*uint32{farm.FertilePlantedCells, farm.BlightedPlants} {
			if count != nil && *count > farm.GetPlantedCells() {
				return false
			}
		}
	}
	for _, fraction := range []*float64{farm.GrowthMin, farm.GrowthMean} {
		if fraction != nil && (!combatNumber(fraction, true) || *fraction > 1) {
			return false
		}
	}
	for _, value := range []*float64{farm.NutritionPerHarvestCell, farm.FertilityFactorMin, farm.FertilityFactorMean, farm.LightFactorMean} {
		if !combatNumber(value, true) {
			return false
		}
	}
	for _, value := range []*float64{farm.Temperature, farm.MinGrowthTemperature, farm.MinOptimalGrowthTemperature, farm.MaxOptimalGrowthTemperature, farm.MaxGrowthTemperature} {
		if !combatNumber(value, false) {
			return false
		}
	}
	return true
}

func validateColonyProduction(v *o.ColonyFactsSnapshot) error {
	for _, issue := range v.Issues {
		if issue.GetField() == "farms" && len(v.Farms) != 0 || issue.GetField() == "cooking" && len(v.Cooking) != 0 || issue.GetField() == "butchering" && len(v.Butchering) != 0 {
			return contract("unavailable production census contains rows")
		}
	}
	farms := map[string]bool{}
	for _, farm := range v.Farms {
		if farm == nil || validID(farm.GetZone().GetId()) != nil || validID(farm.GetCrop()) != nil || farms[farm.GetZone().GetId()] {
			return contract("invalid or duplicate farm")
		}
		farms[farm.GetZone().GetId()] = true
		for _, count := range []*uint32{farm.UsableCells, farm.PlantedCells, farm.FertilePlantedCells, farm.BlightedPlants} {
			if count != nil && uint64(*count) > uint64(v.MapSize.GetWidth())*uint64(v.MapSize.GetHeight()) {
				return contract("farm cells exceed map")
			}
		}
		if !validFarmMeasures(farm) {
			return contract("invalid farm production measure")
		}
	}
	rows := append([]*o.CookingFacts(nil), v.Cooking...)
	for _, b := range v.Butchering {
		if b == nil {
			return contract("nil butcher bench")
		}
		if len(b.HumanStorageCells) > 6 || !combatNumber(b.HumanCorpseNutrition, true) {
			return contract("invalid human butchery census")
		}
		workers := map[string]bool{}
		for _, worker := range b.HumanButchers {
			if worker == nil || validID(worker.PawnId) != nil || workers[worker.PawnId] {
				return contract("invalid human butcher")
			}
			workers[worker.PawnId] = true
			for _, trait := range worker.PawnTraits {
				if validID(trait.GetDefName()) != nil {
					return contract("invalid butcher trait")
				}
			}
		}
		for _, cell := range b.HumanStorageCells {
			if !colonyCell(cell, v.MapSize) {
				return contract("invalid human corpse storage")
			}
		}
		if b.HumanCorpseDef != nil && validID(b.GetHumanCorpseDef()) != nil {
			return contract("invalid human corpse definition")
		}
		rows = append(rows, &o.CookingFacts{Bench: b.Bench, BenchSnapshot: b.BenchSnapshot, Usable: b.Usable, Bills: b.Bills, Recipes: b.Recipes})
	}
	benches := map[string]bool{}
	for _, bench := range rows {
		if bench == nil || bench.Bench == nil || !validRef(bench.Bench) || benches[bench.Bench.GetId()] {
			return contract("invalid production bench")
		}
		benches[bench.Bench.GetId()] = true
		if snapshot := bench.BenchSnapshot; snapshot != nil {
			if !proto.Equal(snapshot.Context, v.Context) || snapshot.GetEntityId() != bench.Bench.GetId() || validID(snapshot.GetToken()) != nil {
				return contract("bill stack snapshot mismatch")
			}
		}
		if len(bench.Bills) > 15 {
			return contract("bill census exceeds bound")
		}
		recipes := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if recipe == nil || recipe.Recipe == nil || validID(recipe.Recipe.GetDefName()) != nil || recipes[recipe.Recipe.GetDefName()] || !proto.Equal(recipe, &o.RecipeState{Recipe: recipe.Recipe, AvailableNow: recipe.AvailableNow, AvailableOnBench: recipe.AvailableOnBench}) {
				return contract("invalid production recipe")
			}
			recipes[recipe.Recipe.GetDefName()] = true
		}
		ids := map[string]bool{}
		for _, bill := range bench.Bills {
			if bill == nil || bill.Recipe == nil || validID(bill.Recipe.GetDefName()) != nil || !proto.Equal(bill, &o.BillState{DefaultIngredients: bill.DefaultIngredients, UnrestrictedWorker: bill.UnrestrictedWorker, ManagedUnchanged: bill.ManagedUnchanged, Id: bill.Id, Recipe: bill.Recipe, Suspended: bill.Suspended, RepeatMode: bill.RepeatMode, RepeatCount: bill.RepeatCount, TargetCount: bill.TargetCount, UnpauseBelow: bill.UnpauseBelow, PauseWhenSatisfied: bill.PauseWhenSatisfied, Paused: bill.Paused, Finished: bill.Finished, Reservations: bill.Reservations, Worker: bill.Worker, IngredientFilter: bill.IngredientFilter}) {
				return contract("invalid production bill")
			}
			if !optionalRef(bill.Worker) {
				return contract("invalid bill worker")
			}
			if filter := bill.IngredientFilter; filter != nil {
				if !proto.Equal(filter, &o.StockpileFilter{AllowedDefNames: filter.AllowedDefNames}) {
					return contract("invalid bill filter")
				}
				seen := map[string]bool{}
				for _, id := range filter.AllowedDefNames {
					if validID(id) != nil || seen[id] {
						return contract("invalid bill filter definition")
					}
					seen[id] = true
				}
			}
			if bill.Id != nil {
				if validID(bill.GetId()) != nil || ids[bill.GetId()] {
					return contract("invalid bill identity")
				}
				ids[bill.GetId()] = true
			}
			for _, n := range []*int32{bill.RepeatCount, bill.TargetCount, bill.UnpauseBelow} {
				if n != nil && *n < 0 {
					return contract("negative bill setting")
				}
			}
		}
		produced := map[string]bool{}
		for _, production := range bench.Production {
			if production == nil || validID(production.GetRecipe()) != nil || !recipes[production.GetRecipe()] || produced[production.GetRecipe()] || production.Available == nil {
				return contract("invalid food recipe output")
			}
			produced[production.GetRecipe()] = true
			defs := map[string]bool{}
			for _, product := range production.Products {
				if product == nil || validID(product.GetDefName()) != nil || defs[product.GetDefName()] || product.Count == nil || product.GetCount() <= 0 || product.Edible == nil || product.NutritionDemandPerDay == nil {
					return contract("invalid food product")
				}
				defs[product.GetDefName()] = true
				if n := product.NutritionDemandPerDay; math.IsNaN(*n) || math.IsInf(*n, 0) || *n < 0 {
					return contract("invalid food product measure")
				}
			}
		}
	}
	return nil
}
