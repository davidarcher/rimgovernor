package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func billForever(mode *string) domain.Fact[bool] {
	if mode == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*mode == "Forever")
}

func billActive(suspended *bool) domain.Fact[bool] {
	if suspended == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(!*suspended)
}

// colonyFoodFields preserves the native optimistic harvest ETA. A planted
// field is future capacity, not an observed delivery of edible stock.
func colonyFoodFields(farms []*o.FarmFacts, definitions []PlanningDefinition) domain.Fact[[]policy.FoodField] {
	if farms == nil {
		return domain.Unknown[[]policy.FoodField]()
	}
	rows := []policy.FoodField{}
	for _, farm := range farms {
		if farm.ZoneId == nil || farm.GrowingCells == nil {
			return domain.Unknown[[]policy.FoodField]()
		}
		crop := policy.CropChoice{Name: farm.GetCrop(), Edible: optional(farm.EdibleCrop)}
		work := domain.Unknown[float64]()
		for _, d := range definitions {
			if d.Name != farm.GetCrop() {
				continue
			}
			crop.GrowDays, crop.HarvestNutrition = d.GrowDays, d.HarvestNutrition
			if days, dk := d.GrowDays.Value(); dk && days > 0 {
				if harvest, hk := d.HarvestWork.Value(); hk {
					work = domain.Known(harvest * float64(farm.GetGrowingCells()) / days)
				}
			}
		}
		rows = append(rows, policy.FoodField{ID: farm.GetZoneId(),
			Plan:              policy.FieldPlan{Crop: crop, Sites: policy.FarmSitePlan{Cells: int(farm.GetGrowingCells())}},
			RemainingGrowDays: optional(farm.HarvestLowerBoundDays), WorkPerDay: work, Open: domain.Known(false)})
	}
	return domain.Known(rows)
}

func colonyFieldCrops(farms []*o.FarmFacts, definitions []PlanningDefinition, usable ...bool) domain.Fact[[]policy.FieldCrop] {
	if farms == nil {
		return domain.Unknown[[]policy.FieldCrop]()
	}
	rows := make([]policy.FieldCrop, 0, len(farms))
	for _, farm := range farms {
		row := policy.FieldCrop{Edible: optional(farm.EdibleCrop), GrowingCells: countFact(farm.GrowingCells)}
		if len(usable) == 1 && usable[0] {
			row.GrowingCells = countFact(farm.UsableCells)
		}
		for _, definition := range definitions {
			if farm.Crop != nil && definition.Name == farm.GetCrop() {
				row.GrowDays = definition.GrowDays
				row.HarvestNutrition = definition.HarvestNutrition
				row.Demand = definition.NutritionDemandPerDay
				break
			}
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}

// ApplyFieldBudget uses the configured reserve target at the review boundary.
func (p *ColonyProjection) ApplyFieldBudget(reserveDays float64) {
	p.Facts.FieldCoverage = policy.FieldCoverage(p.Facts.Colonists, p.FieldCrops, reserveDays)
}

func colonyProduction(v *o.ColonyFactsSnapshot, facts *policy.RoutineFacts) {
	if !hasIssue(v.Issues, "cooking") {
		ready, known := false, true
		for _, bench := range v.Cooking {
			if bench.Usable == nil {
				known = false
				continue
			}
			if !bench.GetUsable() {
				continue
			}
			recipes := map[string]bool{}
			for _, recipe := range bench.Recipes {
				recipes[recipe.Recipe.GetDefName()] = true
			}
			for _, bill := range bench.Bills {
				if !recipes[bill.Recipe.GetDefName()] {
					continue
				}
				if bill.Suspended == nil {
					known = false
				} else if !bill.GetSuspended() {
					ready = true
				}
			}
		}
		if ready || known {
			facts.Cooking = domain.Known(ready)
		}
	}
}

func colonyProductionBenches(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.ProductionBench] {
	if hasIssue(v.Issues, "cooking") || hasIssue(v.Issues, "butchering") {
		return domain.Unknown[[]policy.ProductionBench]()
	}
	var rows []policy.ProductionBench
	add := func(entity *o.EntityRef, usable *bool, recipes []*o.RecipeState, bills []*o.BillState, production []*o.FoodProduction, butcher bool, room *string) {
		row := policy.ProductionBench{ID: entity.GetId(), Definition: entity.GetDefName(), Usable: optional(usable), Butcher: butcher, Room: optional(room)}
		if entity.Snapshot != nil {
			row.Token = optional(entity.Snapshot.Token)
		}
		for _, r := range recipes {
			recipe := policy.ProductionRecipe{Name: r.Recipe.GetDefName()}
			mealRecipeFacts(r, &recipe)
			if r.AvailableNow != nil && r.AvailableOnBench != nil {
				recipe.Available = domain.Known(r.GetAvailableNow() && r.GetAvailableOnBench())
			}
			for _, p := range production {
				if p.GetRecipe() == recipe.Name {
					if p.Available != nil {
						recipe.Available = domain.Known(p.GetAvailable() && r.GetAvailableNow() && r.GetAvailableOnBench())
					}
					for _, product := range p.Products {
						recipe.Products = append(recipe.Products, policy.ProductionProduct{Name: product.GetDefName(), Nutrition: optional(product.Nutrition), Demand: optional(product.NutritionDemandPerDay), RotDays: optional(product.RotDays), Edible: optional(product.Edible), Perishable: optional(product.Perishable)})
					}
				}
			}
			row.Recipes = append(row.Recipes, recipe)
		}
		for _, b := range bills {
			row.Bills = append(row.Bills, policy.ExistingProductionBill{ID: b.GetId(), Managed: optional(b.ManagedUnchanged), Active: billActive(b.Suspended), Recipe: b.Recipe.GetDefName(), TargetCount: optional(b.TargetCount), Forever: billForever(b.RepeatMode), Humanlike: len(b.GetIngredientFilter().GetAllowedDefNames()) > 0})
		}
		rows = append(rows, row)
	}
	for _, b := range v.Cooking {
		add(b.Bench, b.Usable, b.Recipes, b.Bills, b.Production, false, b.RoomId)
	}
	for _, b := range v.Butchering {
		add(b.Bench, b.Usable, b.Recipes, b.Bills, nil, true, b.RoomId)
		row := &rows[len(rows)-1]
		row.HumanCorpseNutrition = optional(b.HumanCorpseNutrition)
		row.HumanStorageReady = optional(b.HumanStorageReady)
		row.HumanCorpseDef = b.GetHumanCorpseDef()
		for _, cell := range b.HumanStorageCells {
			row.HumanStorageCells = append(row.HumanStorageCells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		for _, candidate := range b.HumanButchers {
			var traits []policy.PawnTrait
			for _, name := range candidate.Traits {
				traits = append(traits, policy.PawnTrait{Name: name})
			}
			row.HumanButchers = append(row.HumanButchers, policy.HumanButcherCandidate{ID: policy.PawnID(candidate.PawnId), Traits: domain.Known(traits), PreceptAcceptable: optional(candidate.PreceptAcceptable), CanWork: optional(candidate.CanWork)})
		}
	}
	return domain.Known(rows)
}

// colonyCalendar projects the native food climate into the routine calendar
// fact. Every day count and the sowing flag must be present; the season
// name and day of year are informational and default when absent.
func colonyCalendar(climate *o.FoodClimate) domain.Fact[policy.Calendar] {
	if climate.GrowingDays == nil || climate.GrowingDaysRemaining == nil || climate.GrowingDaysUntil == nil || climate.NonGrowingDays == nil || climate.SowingNow == nil {
		return domain.Unknown[policy.Calendar]()
	}
	c := policy.Calendar{Season: climate.GetSeason(), DayOfYear: int64(climate.GetDayOfYear()), GrowingDays: climate.GetGrowingDays(), GrowingDaysRemaining: climate.GetGrowingDaysRemaining(), GrowingDaysUntil: climate.GetGrowingDaysUntil(), NonGrowingDays: climate.GetNonGrowingDays(), Sowing: climate.GetSowingNow()}
	if !c.Valid() {
		return domain.Unknown[policy.Calendar]()
	}
	return domain.Known(c)
}

func zoneProduction(farms []*o.FarmFacts, facts *policy.RoutineFacts) {
	if farms != nil {
		var growing int64
		known := true
		for _, farm := range farms {
			if farm.EdibleCrop == nil {
				known = false
				continue
			}
			if !farm.GetEdibleCrop() {
				continue
			}
			if farm.GrowingCells == nil {
				known = false
				continue
			}
			growing += int64(farm.GetGrowingCells())
		}
		if known {
			facts.GrowingCells = domain.Known(growing)
		}
	}
}
