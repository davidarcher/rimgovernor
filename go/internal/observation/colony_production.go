package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// billIngredients is the bill's allowed defs; a butcher bill's filter is the
// corpse selection, not an ingredient list, so it stays unknown.
func billIngredients(b *o.BillState, role domain.RecipeRole) domain.Fact[[]string] {
	if b.IngredientFilter == nil || role == domain.RoleButcherFlesh {
		return domain.Unknown[[]string]()
	}
	return domain.Known(append([]string(nil), b.IngredientFilter.AllowedDefNames...))
}

func billForever(mode *op.RepeatMode) domain.Fact[bool] {
	if mode == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(*mode == op.RepeatMode_REPEAT_MODE_FOREVER)
}

func repeatMode(mode *op.RepeatMode) domain.Fact[string] {
	if mode == nil {
		return domain.Unknown[string]()
	}
	return domain.Known(bridge.RepeatModeName(*mode))
}

func billActive(suspended *bool) domain.Fact[bool] {
	if suspended == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(!*suspended)
}

// humanCorpseNutrition is the nutrition of the fresh humanlike corpses near a
// bench: each race's live MeatAmount sum times the Nutrition of that race's
// meat def; unknown when a race's meat shows no nutrition.
func humanCorpseNutrition(meat []*o.HumanCorpseMeat, catalog *bridge.DefinitionCatalog) domain.Fact[float64] {
	total := 0.0
	for _, m := range meat {
		per, ok, err := catalog.RaceMeatNutrition(m.GetRace())
		if err != nil || !ok {
			return domain.Unknown[float64]()
		}
		total += m.GetMeatAmount() * per
	}
	return domain.Known(total)
}

// farmGrowth is a growing zone's raw crop facts for policy.
func farmGrowth(farm *o.FarmFacts, definitions []PlanningDefinition) policy.CropGrowth {
	lo, loOptimal, hiOptimal, hi := domain.Unknown[float64](), domain.Unknown[float64](), domain.Unknown[float64](), domain.Unknown[float64]()
	for _, d := range definitions {
		if d.Name == farm.GetCrop() {
			lo, loOptimal, hiOptimal, hi = d.MinGrowthTemperature, d.MinOptimalGrowthTemperature, d.MaxOptimalGrowthTemperature, d.MaxGrowthTemperature
		}
	}
	return policy.CropGrowth{Planted: countFact(farm.PlantedCells), FertilePlanted: countFact(farm.FertilePlantedCells),
		GrowthMean: optional(farm.GrowthMean), FertilityMean: optional(farm.FertilityFactorMean), Temperature: optional(farm.Temperature),
		MinGrowth: lo, MinOptimal: loOptimal,
		MaxOptimal: hiOptimal, MaxGrowth: hi}
}

// colonyFoodFields computes each field's harvest lead in policy from the raw
// growth facts, the current temperature and the calendar; an unknown fact
// leaves the lead unknown. A planted field is future capacity, not an
// observed delivery of edible stock. Exposure reads each zone's roofs from the
// planning window's cells (unknown without the window or with an unread roof)
// and its blight from the zone's planted and blighted counts.
func colonyFoodFields(farms []*o.FarmFacts, definitions []PlanningDefinition, calendar domain.Fact[policy.Calendar], cells []policy.SiteCell) domain.Fact[[]policy.FoodField] {
	if farms == nil {
		return domain.Unknown[[]policy.FoodField]()
	}
	zones := zoneRoofs(cells)
	rows := []policy.FoodField{}
	for _, farm := range farms {
		growth := farmGrowth(farm, definitions)
		growing, growingKnown := growth.GrowingCells().Value()
		if farm.Zone == nil || !growingKnown {
			return domain.Unknown[[]policy.FoodField]()
		}
		crop := policy.CropChoice{Name: farm.GetCrop(), Edible: optional(farm.EdibleCrop)}
		work := domain.Unknown[float64]()
		for _, d := range definitions {
			if d.Name != farm.GetCrop() {
				continue
			}
			crop.GrowDays, crop.HarvestNutrition, crop.RotDays, crop.Perishable = d.GrowDays, d.HarvestNutrition, d.HarvestRotDays, d.HarvestPerishable
			crop.RawPreferred = d.RawPreferred
			if days, dk := d.GrowDays.Value(); dk && days > 0 {
				if harvest, hk := d.HarvestWork.Value(); hk {
					work = domain.Known(harvest * float64(growing) / days)
				}
			}
		}
		rows = append(rows, policy.FoodField{ID: farm.GetZone().GetId(),
			Plan:              policy.FieldPlan{Crop: crop, Sites: policy.FarmSitePlan{Cells: int(growing)}},
			RemainingGrowDays: policy.HarvestLeadDays(growth, crop.GrowDays, calendar), WorkPerDay: work,
			Exposure: policy.FieldExposure{ZoneCells: zones[farm.GetZone().GetId()].cells, UnroofedCells: zones[farm.GetZone().GetId()].unroofed,
				Planted: countFact(farm.PlantedCells), Blighted: countFact(farm.BlightedPlants)}})
	}
	return domain.Known(rows)
}

// zoneRoof counts a zone's held cells and those under open sky.
type zoneRoof struct{ cells, unroofed domain.Fact[int64] }

// zoneRoofs counts each growing zone's cells in the planning window and the
// ones with no roof. A zone with a cell whose roof is unread is unknown: a
// partial count would pass for the field's exposure. Without the window no
// zone has a row, so every exposure reads unknown.
func zoneRoofs(cells []policy.SiteCell) map[string]zoneRoof {
	type tally struct {
		cells, unroofed int64
		unknown         bool
	}
	counts := map[string]*tally{}
	for _, cell := range cells {
		if zoned, _ := cell.Zone.Value(); !zoned {
			continue
		}
		id, ik := cell.ZoneID.Value()
		if !ik {
			continue
		}
		t := counts[id]
		if t == nil {
			t = &tally{}
			counts[id] = t
		}
		roofed, rk := cell.Roofed.Value()
		switch {
		case !rk:
			t.unknown = true
		case !roofed:
			t.unroofed++
		}
		t.cells++
	}
	out := make(map[string]zoneRoof, len(counts))
	for id, t := range counts {
		if !t.unknown {
			out[id] = zoneRoof{cells: domain.Known(t.cells), unroofed: domain.Known(t.unroofed)}
		}
	}
	return out
}

func colonyFieldCrops(farms []*o.FarmFacts, definitions []PlanningDefinition, usable ...bool) domain.Fact[[]policy.FieldCrop] {
	if farms == nil {
		return domain.Unknown[[]policy.FieldCrop]()
	}
	rows := make([]policy.FieldCrop, 0, len(farms))
	for _, farm := range farms {
		row := policy.FieldCrop{Edible: optional(farm.EdibleCrop), GrowingCells: farmGrowth(farm, definitions).GrowingCells()}
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

func colonyProduction(v *o.ColonyFactsSnapshot, facts *policy.RoundsFacts) {
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

// productionProduct is a recipe product: the frame's varying facts (count,
// demand, storage room) joined to what its def row says (nutrition from the
// stat table, rot days, baby edibility). Without a catalog the
// static facts stay unknown. The game shows no Nutrition stat for a def
// that gives none: that nutrition stays unknown, it is not zero.
func productionProduct(product *o.FoodProduct, catalog *bridge.DefinitionCatalog) (policy.ProductionProduct, error) {
	name := product.GetDefName()
	row := policy.ProductionProduct{Name: name, Demand: optional(product.NutritionDemandPerDay), Edible: optional(product.Edible), Storable: optional(product.Storable)}
	if catalog == nil {
		return row, nil
	}
	days, perishable, err := catalog.RotDays(name)
	if err != nil {
		return row, err
	}
	row.Perishable = domain.Known(perishable)
	if perishable {
		row.RotDays = domain.Known(days)
	}
	baby, err := catalog.BabyEdible(name)
	if err != nil {
		return row, err
	}
	row.BabyEdible = domain.Known(baby)
	if row.Kind, err = catalog.FoodKindOf(name); err != nil {
		return row, err
	}
	if nutrition, err := catalog.StatValue(name, "", bridge.StatNutrition); err == nil {
		row.Nutrition = domain.Known(float64(nutrition))
	}
	return row, nil
}

// heldPrecepts is the pawn's own ideoligion and precepts from its row's
// policy inputs; unknown while the table lacks the row or the inputs.
func heldPrecepts(pawns bridge.Pawns, id string) domain.Fact[policy.HeldPrecepts] {
	row, ok := pawns.Get(id)
	if !ok || row == nil || row.Settings == nil || row.Settings.PolicyInputs == nil {
		return domain.Unknown[policy.HeldPrecepts]()
	}
	p := row.Settings.PolicyInputs
	return domain.Known(policy.HeldPrecepts{Ideo: p.GetIdeoId(), Defs: p.Precepts})
}

func colonyProductionBenches(v *o.ColonyFactsSnapshot, buildings bridge.Buildings, pawns bridge.Pawns, catalog *bridge.DefinitionCatalog) (domain.Fact[[]policy.ProductionBench], error) {
	if hasIssue(v.Issues, "cooking") || hasIssue(v.Issues, "butchering") || !headed(buildings, v.Cooking, (*o.CookingFacts).GetBench) || !headed(buildings, v.Butchering, (*o.ButcheringFacts).GetBench) {
		return domain.Unknown[[]policy.ProductionBench](), nil
	}
	var failed error
	var rows []policy.ProductionBench
	add := func(bench *c.Ref, snapshot *o.SnapshotRef, usable *bool, recipes []*o.RecipeState, bills []*o.BillState, production []*o.FoodProduction, butcher bool, room *c.Ref) {
		row := policy.ProductionBench{ID: bench.GetId(), Definition: buildings.Entity(bench).GetDefName(), Usable: optional(usable), Butcher: butcher, Room: optionalRef(room)}
		if snapshot != nil {
			row.Token = optional(snapshot.Token)
		}
		for _, r := range recipes {
			recipe := policy.ProductionRecipe{Name: r.Recipe.GetDefName()}
			role, err := catalog.RecipeRole(recipe.Name)
			if failed == nil {
				failed = err
			}
			recipe.Role = role
			bulk, err := catalog.RecipeBulk(recipe.Name)
			if failed == nil {
				failed = err
			}
			recipe.Bulk = bulk
			meal, err := catalog.RecipeMealFacts(recipe.Name, row.Definition)
			if failed == nil {
				failed = err
			}
			recipe.Mood, recipe.NutrientEfficiency, recipe.WorkPerNutrition = meal.Mood, meal.NutrientEfficiency, meal.WorkPerNutrition
			recipe.NeedsPower, recipe.IngredientClasses, recipe.CookSkillFloor = meal.NeedsPower, meal.IngredientClasses, meal.CookSkillFloor
			if r.AvailableNow != nil && r.AvailableOnBench != nil {
				recipe.Available = domain.Known(r.GetAvailableNow() && r.GetAvailableOnBench())
			}
			for _, p := range production {
				if p.GetRecipe() == recipe.Name {
					if p.Available != nil {
						recipe.Available = domain.Known(p.GetAvailable() && r.GetAvailableNow() && r.GetAvailableOnBench())
					}
					for _, product := range p.Products {
						row, err := productionProduct(product, catalog)
						if failed == nil {
							failed = err
						}
						recipe.Products = append(recipe.Products, row)
					}
				}
			}
			row.Recipes = append(row.Recipes, recipe)
		}
		for _, b := range bills {
			role, err := catalog.RecipeRole(b.Recipe.GetDefName())
			if failed == nil {
				failed = err
			}
			row.Bills = append(row.Bills, policy.ExistingProductionBill{Role: role, DefaultIngredients: optional(b.DefaultIngredients), UnrestrictedWorker: optional(b.UnrestrictedWorker), Worker: domain.Known(b.GetWorker().GetId()), RepeatMode: repeatMode(b.RepeatMode), Ingredients: billIngredients(b, role), ID: b.GetId(), Managed: optional(b.ManagedUnchanged), Active: billActive(b.Suspended), Recipe: b.Recipe.GetDefName(), TargetCount: optional(b.TargetCount), Forever: billForever(b.RepeatMode), Humanlike: butcher && len(b.GetIngredientFilter().GetAllowedDefNames()) > 0})
		}
		rows = append(rows, row)
	}
	for _, b := range v.Cooking {
		add(b.Bench, b.BenchSnapshot, b.Usable, b.Recipes, b.Bills, b.Production, false, b.Room)
	}
	for _, b := range v.Butchering {
		add(b.Bench, b.BenchSnapshot, b.Usable, b.Recipes, b.Bills, nil, true, b.Room)
		row := &rows[len(rows)-1]
		row.HumanCorpseNutrition = humanCorpseNutrition(b.HumanCorpseMeat, catalog)
		for _, candidate := range b.HumanButchers {
			var traits []policy.PawnTrait
			for _, t := range candidate.PawnTraits {
				trait := policy.PawnTrait{Name: t.GetDefName(), Degree: int(t.GetDegree())}
				if err := resolveTrait(catalog, &trait); err != nil && failed == nil {
					failed = err
				}
				traits = append(traits, trait)
			}
			row.HumanButchers = append(row.HumanButchers, policy.HumanButcherCandidate{ID: policy.PawnID(candidate.PawnId), Traits: domain.Known(traits), Held: heldPrecepts(pawns, candidate.PawnId), CanWork: optional(candidate.CanWork)})
		}
	}
	if failed != nil {
		return domain.Unknown[[]policy.ProductionBench](), failed
	}
	return domain.Known(rows), nil
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

func zoneProduction(farms []*o.FarmFacts, definitions []PlanningDefinition, facts *policy.RoundsFacts) {
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
			cells, cellsKnown := farmGrowth(farm, definitions).GrowingCells().Value()
			if !cellsKnown {
				known = false
				continue
			}
			growing += cells
		}
		if known {
			facts.GrowingCells = domain.Known(growing)
		}
	}
}
