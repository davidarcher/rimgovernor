package bridge

import (
	"math"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyProduction(v *o.ColonyFactsSnapshot) error {
	if len(v.Farms) > 256 || len(v.Cooking) > 256 || len(v.Butchering) > 256 {
		return contract("production census exceeds bound")
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "farms" && len(v.Farms) != 0 || issue.GetField() == "cooking" && len(v.Cooking) != 0 || issue.GetField() == "butchering" && len(v.Butchering) != 0 {
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
	rows := append([]*o.CookingFacts(nil), v.Cooking...)
	for _, b := range v.Butchering {
		if b == nil {
			return contract("nil butcher bench")
		}
		if len(b.HumanButchers) > 256 || len(b.HumanStorageCells) > 6 || !combatNumber(b.HumanCorpseNutrition, true) {
			return contract("invalid human butchery census")
		}
		workers := map[string]bool{}
		for _, worker := range b.HumanButchers {
			if worker == nil || validID(worker.PawnId) != nil || workers[worker.PawnId] || len(worker.Traits) > 256 {
				return contract("invalid human butcher")
			}
			workers[worker.PawnId] = true
			for _, trait := range worker.Traits {
				if validID(trait) != nil {
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
		rows = append(rows, &o.CookingFacts{Bench: b.Bench, Usable: b.Usable, Bills: b.Bills, Recipes: b.Recipes})
	}
	benches := map[string]bool{}
	for _, bench := range rows {
		if bench == nil || bench.Bench == nil || validID(bench.Bench.GetId()) != nil || validID(bench.Bench.GetDefName()) != nil || bench.Bench.MapId == nil || bench.Bench.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(bench.Bench.Position, v.MapSize) || benches[bench.Bench.GetId()] || bench.Bench.Label != nil {
			return contract("invalid production bench")
		}
		benches[bench.Bench.GetId()] = true
		if snapshot := bench.Bench.Snapshot; snapshot != nil {
			if !proto.Equal(snapshot.Context, v.Context) || snapshot.GetEntityId() != bench.Bench.GetId() || validID(snapshot.GetToken()) != nil {
				return contract("bill stack snapshot mismatch")
			}
		}
		if len(bench.Recipes) > 256 || len(bench.Bills) > 15 || len(bench.Production) > 256 {
			return contract("bill census exceeds bound")
		}
		recipes := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if recipe == nil || recipe.Recipe == nil || validID(recipe.Recipe.GetDefName()) != nil || recipes[recipe.Recipe.GetDefName()] || !proto.Equal(recipe, &o.RecipeState{Recipe: recipe.Recipe, AvailableNow: recipe.AvailableNow, AvailableOnBench: recipe.AvailableOnBench, Mood: recipe.Mood, IngredientClasses: recipe.IngredientClasses, NutrientEfficiency: recipe.NutrientEfficiency, WorkPerNutrition: recipe.WorkPerNutrition, NeedsPower: recipe.NeedsPower, Skills: recipe.Skills}) {
				return contract("invalid production recipe")
			}
			if err := validateMealRecipe(recipe); err != nil {
				return err
			}
			recipes[recipe.Recipe.GetDefName()] = true
		}
		ids := map[string]bool{}
		for i, bill := range bench.Bills {
			if bill == nil || bill.Recipe == nil || validID(bill.Recipe.GetDefName()) != nil || !proto.Equal(bill, &o.BillState{ManagedUnchanged: bill.ManagedUnchanged, Id: bill.Id, Index: bill.Index, Recipe: bill.Recipe, Suspended: bill.Suspended, RepeatMode: bill.RepeatMode, RepeatCount: bill.RepeatCount, TargetCount: bill.TargetCount, UnpauseBelow: bill.UnpauseBelow, PauseWhenSatisfied: bill.PauseWhenSatisfied, Paused: bill.Paused, Finished: bill.Finished, WorkerId: bill.WorkerId, IngredientFilter: bill.IngredientFilter}) {
				return contract("invalid production bill")
			}
			if bill.WorkerId != nil && bill.GetWorkerId() != "" && validID(bill.GetWorkerId()) != nil {
				return contract("invalid bill worker")
			}
			if filter := bill.IngredientFilter; filter != nil {
				if bill.Recipe.GetDefName() != "ButcherCorpseFlesh" || len(filter.AllowedDefNames) > 256 || !proto.Equal(filter, &o.StockpileFilter{AllowedDefNames: filter.AllowedDefNames}) {
					return contract("invalid butcher filter")
				}
				seen := map[string]bool{}
				for _, id := range filter.AllowedDefNames {
					if validID(id) != nil || seen[id] {
						return contract("invalid butcher filter definition")
					}
					seen[id] = true
				}
			}
			if bill.Id != nil {
				if validID(bill.GetId()) != nil || ids[bill.GetId()] || bill.Index == nil || int(bill.GetIndex()) != i {
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
			if production == nil || validID(production.GetRecipe()) != nil || !recipes[production.GetRecipe()] || produced[production.GetRecipe()] || production.Available == nil || len(production.Products) > 256 {
				return contract("invalid food recipe output")
			}
			produced[production.GetRecipe()] = true
			defs := map[string]bool{}
			for _, product := range production.Products {
				if product == nil || validID(product.GetDefName()) != nil || defs[product.GetDefName()] || product.Count == nil || product.GetCount() <= 0 || product.Edible == nil || product.Nutrition == nil || product.NutritionDemandPerDay == nil {
					return contract("invalid food product")
				}
				defs[product.GetDefName()] = true
				for _, n := range []*float64{product.Nutrition, product.NutritionDemandPerDay, product.RotDays} {
					if n != nil && (math.IsNaN(*n) || math.IsInf(*n, 0) || *n < 0) {
						return contract("invalid food product measure")
					}
				}
			}
		}
	}
	return nil
}
