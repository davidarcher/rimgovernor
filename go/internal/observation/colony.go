package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type PlanningDefinition struct {
	HarvestWork                                                     domain.Fact[float64]
	RawPreferred, DietAllowed, RequiresPollution, RequiresCleanSoil domain.Fact[bool]
	Name                                                            string
	Edible                                                          domain.Fact[bool]
	Stuff                                                           domain.Fact[string]
	Available                                                       domain.Fact[bool]
	ConstructionSkill                                               domain.Fact[int32]
	NeedsPower                                                      domain.Fact[bool]
	// Research names the ResearchProjectDefs the definition requires, as the
	// native definition declares them (unfinished ones make it unavailable).
	Research                                                                              []string
	Costs                                                                                 domain.Fact[[]policy.Amount]
	Size                                                                                  domain.Fact[policy.Bounds]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, NutritionDemandPerDay domain.Fact[float64]
	// Crop sow tags and minimum glow; grower sow tag and fertility; building
	// power draw and glow radius, as the native definition declares them.
	SowTags                                          domain.Fact[[]string]
	GrowMinGlow, PowerW, GrowerFertility, GlowRadius domain.Fact[float64]
	SowTag                                           domain.Fact[string]
	// Floor definition facts (issue #6 slice 4): Terrain marks a TerrainDef
	// and the stats are what a laid floor carries.
	Terrain                           domain.Fact[bool]
	Cleanliness, Beauty, Flammability domain.Fact[float64]
	PathCost                          domain.Fact[int32]
}
type ColonyProjection struct {
	FoodFields          domain.Fact[[]policy.FoodField]
	FoodChannels        domain.Fact[FoodChannels]
	ProductionBenches   domain.Fact[[]policy.ProductionBench]
	ButcheringBenches   domain.Fact[[]CookingBench]
	FoodAtRiskNutrition domain.Fact[float64]
	ZoneMapToken        domain.Fact[string]
	CropClimate         policy.CropClimate

	PendingHunts domain.Fact[int]

	Acquisition                            domain.Fact[[]policy.AcquisitionSource]
	PendingFoodNutrition, PendingWoodUnits domain.Fact[float64]
	WorkPawns                              domain.Fact[[]policy.WorkPawn]
	FieldCrops                             domain.Fact[[]policy.FieldCrop]
	FieldCapacityCrops                     domain.Fact[[]policy.FieldCrop]
	CookingBenches                         domain.Fact[[]CookingBench]
	PowerPlanning                          domain.Fact[policy.PowerTopology]
	Rooms                                  domain.Fact[policy.RoomObservation]
	Identity                               Identity
	// PlayerTechLevel is the player faction's native TechLevel name, which
	// selects the starter shelter's shape family.
	PlayerTechLevel domain.Fact[string]
	Facts           policy.RoutineFacts
	Workers         domain.Fact[int]
	Bounds          policy.Bounds
	Center          domain.Cell
	// Region is the observed planning window; cells absent inside it are
	// fogged, cells outside it were never read.
	Region policy.Rectangle
	Cells  []policy.SiteCell
	// Window is the window's provenance when a PlanningWindowSource served
	// it (Source set); empty when the reply listed the cells itself.
	Window facts.Held[PlanningCells]
	// Farms lists observed growing zones by native id and current crop.
	Farms []FarmZoneFact
	// Environment is the controlled-growing census inside the planning region.
	Environment        domain.Fact[policy.ControlledEnvironment]
	Definitions        []PlanningDefinition
	FoodSupply         domain.Fact[policy.FoodSupply]
	CombinedFoodSupply domain.Fact[policy.FoodSupply]
	// Resources is the accessible colony stock census by definition; a
	// definition absent from a known census is known zero.
	Resources domain.Fact[map[policy.Resource]int64]
}

// ResourceStock reports the accessible stock of one definition, unknown when
// the census itself is unknown.
func (r ColonyProjection) ResourceStock(name policy.Resource) domain.Fact[int64] {
	stock, known := r.Resources.Value()
	if !known {
		return domain.Unknown[int64]()
	}
	return domain.Known(stock[name])
}

// DefinitionAvailable is one planning definition's native availability,
// unknown when the census did not describe it.
func (r ColonyProjection) DefinitionAvailable(name string) domain.Fact[bool] {
	for _, d := range r.Definitions {
		if d.Name == name {
			return d.Available
		}
	}
	return domain.Unknown[bool]()
}

// GeneratorOptions pairs the power family's generator definitions with their
// native availability and observed fuel stock.
func (r ColonyProjection) GeneratorOptions() []policy.GeneratorOption {
	available := map[string]domain.Fact[bool]{}
	for _, d := range r.Definitions {
		available[d.Name] = d.Available
	}
	return policy.DefaultGeneratorOptions(func(name string) domain.Fact[bool] {
		if fact, ok := available[name]; ok {
			return fact
		}
		return domain.Unknown[bool]()
	}, r.ResourceStock)
}

type FarmZoneFact struct {
	ID, Crop    string
	UsableCells domain.Fact[uint32]
}

type CookingBench struct {
	ID, Definition string
	Usable         domain.Fact[bool]
	// Room is the native room census identity the bench stands in, unknown
	// for a bench outdoors or when the native read left it out.
	Room domain.Fact[string]
}

func optional[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}
func countFact(p *uint32) domain.Fact[int64] {
	if p == nil {
		return domain.Unknown[int64]()
	}
	return domain.Known(int64(*p))
}
func hasIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}
func nativePresence(value *string, issues []*o.ReadIssue, field string) domain.Fact[bool] {
	return bridge.CellPresence(value, issues, field, false)
}

// DecodeColony projects only same-tick validated facts. Native raw food runway
// cannot stand in for the diet/rot/competing-feed forecast needed by FoodDays.
func DecodeColony(reply *o.ColonyFactsReply, expected Identity) (ColonyProjection, error) {
	if err := expected.Validate(); err != nil {
		return ColonyProjection{}, err
	}
	if reply == nil {
		return ColonyProjection{}, ErrContract
	}
	if reply.GetUnavailable() != nil {
		return ColonyProjection{}, bridge.ErrUnavailable
	}
	if reply.GetFailure() != nil {
		return ColonyProjection{}, bridge.ErrRefused
	}
	v := reply.GetObserved()
	id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
	if err := bridge.ValidateColonyFacts(v, id); err != nil {
		return ColonyProjection{}, err
	}
	identity, err := contextIdentity(v.Context)
	if err != nil {
		return ColonyProjection{}, err
	}
	// The colony facts row may come from the step's fact cache, behind the
	// expected tick by up to its family's tolerance (#306).
	if !identity.Tick.FreshFor(expected.Tick) && !bridge.FactColony.Fresh(int64(identity.Tick), int64(expected.Tick)) {
		return ColonyProjection{}, ErrChanged
	}
	if generation, known := expected.NativeGeneration.Value(); known {
		if actual, observed := identity.NativeGeneration.Value(); observed && actual != generation {
			return ColonyProjection{}, ErrChanged
		}
	}
	r := ColonyProjection{Identity: identity, Bounds: policy.Bounds{Width: int32(v.MapSize.GetWidth()), Height: int32(v.MapSize.GetHeight())}, Center: domain.Cell{X: v.Center.GetX(), Z: v.Center.GetZ()}}
	r.PlayerTechLevel = optional(v.PlayerTechLevel)
	r.FoodChannels = colonyFoodChannels(v.FoodChannels)
	r.Facts = policy.RoutineFacts{Colonists: countFact(v.ColonistCount), BedCapacity: countFact(v.BedCapacity), IndoorCapacity: countFact(v.IndoorSleepingCapacity), SleepingMin: optional(v.SleepingTemperatureMinC), SleepingMax: optional(v.SleepingTemperatureMaxC), OutdoorTemperature: optional(v.OutdoorTemperatureC), FoodStorage: optional(v.FoodStorage)}
	if channels, known := r.FoodChannels.Value(); known {
		r.Facts.PenGrazing = domain.Known(channels.Grazing)
	}
	if development := v.GetDevelopment().GetObserved(); development != nil {
		power := make([]policy.PowerBuilding, 0, len(development.Power))
		topology := policy.PowerTopology{}
		geometryKnown := true
		if !hasIssue(v.Issues, "environment") {
			blackout, eclipse := false, false
			for _, condition := range v.Environment {
				blackout = blackout || condition.GetDefName() == "SolarFlare"
				eclipse = eclipse || condition.GetDefName() == "Eclipse"
			}
			topology.Blackout, topology.Eclipse = domain.Known(blackout), domain.Known(eclipse)
		}
		for _, row := range development.Power {
			s := row.Building.Service
			power = append(power, policy.PowerBuilding{BaseW: optional(row.BaseW), OutputW: optional(s.PowerOutputW), Powered: optional(s.PowerOn), Connected: optional(s.Connected), Network: optional(s.PowerNetId), Forbidden: optional(row.Building.Settings.Forbidden), SwitchedOn: optional(s.SwitchedOn),
				Fuel: optional(s.Fuel), TargetFuel: optional(s.TargetFuel), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: append([]string(nil), s.AllowedFuelDefs...),
				Stored: optional(row.StoredWattDays), Capacity: optional(row.CapacityWattDays), RainVulnerable: optional(row.RainVulnerable), Roofed: optional(row.Roofed)})
			ref := row.Building.Building
			geometryKnown = geometryKnown && ref.DefName != nil && ref.Position != nil && len(row.Building.OccupiedCells) > 0
			site := policy.PowerSite{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, PowerBuilding: power[len(power)-1]}
			for _, c := range row.Building.OccupiedCells {
				site.Occupied = append(site.Occupied, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			topology.Buildings = append(topology.Buildings, site)
		}
		for _, row := range development.Furniture {
			topology.Conduits = append(topology.Conduits, domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()})
			if row.Building.GetDefName() == "PowerConduit" {
				topology.UnsafeConduits = append(topology.UnsafeConduits, topology.Conduits[len(topology.Conduits)-1])
			}
		}
		for _, row := range development.Networks {
			topology.Networks = append(topology.Networks, policy.PowerNetworkFact{ID: row.GetId(), GenerationW: optional(row.GenerationW), ConsumptionW: optional(row.ConsumptionW), StoredWD: optional(row.StoredWattDays), CapacityWD: optional(row.CapacityWattDays)})
		}
		for _, row := range development.Geysers {
			ref := row.GetGeyser()
			geyser := policy.PowerGeyser{ID: ref.GetId(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Occupied: row.GetOccupied()}
			for _, c := range row.Cells {
				geyser.Cells = append(geyser.Cells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			topology.Geysers = append(topology.Geysers, geyser)
		}
		if geometryKnown {
			r.PowerPlanning = domain.Known(topology)
		}
		r.Facts.PowerRequired, r.Facts.PowerHeadroom, r.Facts.DisabledConsumers = policy.PowerCoverage(domain.Known(power))
		r.Facts.PowerWeatherSafe = policy.PowerWeatherSafe(power)
		if len(topology.UnsafeConduits) > 0 {
			r.Facts.PowerWeatherSafe = domain.Known(false)
		}
		if development.ShortCircuitTick != nil {
			r.Facts.ShortCircuitTick = domain.Known(domain.Tick(development.GetShortCircuitTick()))
		}
	}
	if climate := v.FoodClimate; climate != nil && !hasIssue(v.Issues, "food_climate") {
		r.CropClimate = policy.CropClimate{Sowing: optional(climate.SowingNow), DaysRemaining: optional(climate.GrowingDaysRemaining)}
		r.Facts.Calendar = colonyCalendar(climate)
	}
	colonyAcquisition(v, &r)
	colonyProduction(v, &r.Facts)
	r.ProductionBenches = colonyProductionBenches(v)
	r.Facts.TradeMealIngredients = policy.TradeMealIngredients(r.ProductionBenches)
	if !hasIssue(v.Issues, "butchering") {
		benches := []CookingBench{}
		for _, b := range v.Butchering {
			benches = append(benches, CookingBench{ID: b.Bench.GetId(), Definition: b.Bench.GetDefName(), Usable: optional(b.Usable), Room: optional(b.RoomId)})
		}
		r.ButcheringBenches = domain.Known(benches)
	}
	colonyDisaster(v, &r.Facts)
	r.Facts.Comfort = colonyComfort(v)
	r.Facts.BasicComfort = r.Facts.Comfort
	r.Facts.HomeCoverage = colonyHomeCoverage(v)
	r.Facts.StoneStructures = colonyStoneStructures(v)
	r.Facts.Sleeping = colonySleeping(v)
	r.Facts.AnimalUpkeep.Animals = colonyAnimals(v)
	r.Facts.AnimalUpkeep.WildAnimals = colonyWildAnimals(v)
	r.Facts.Waste = colonyWaste(v)
	r.Facts.Blight = colonyBlight(v)
	r.Facts.Upkeep = colonyUpkeep(v)
	r.Facts.MedicalReserve = colonyMedicalReserve(v)
	// The dialog section is present exactly while a force-pausing choice
	// dialog is open (#156); native omits it otherwise.
	r.Facts.ChoiceDialog = domain.Known(v.Dialog != nil)
	letters := make([]policy.JoinerLetterOffer, 0, len(v.JoinerLetters))
	for _, row := range v.JoinerLetters {
		letters = append(letters, policy.JoinerLetterOffer{ID: row.GetLetterId(), Token: row.GetSnapshotToken(), Pawn: domain.PawnID(row.GetPawnId()), Expires: domain.Tick(row.GetExpiresTick()), Label: row.GetAcceptLabel(), CanAccept: row.GetCanAccept()})
	}
	r.Facts.JoinerLetters = domain.Known(letters)
	if v.Naming != nil {
		r.Facts.ColonyNaming = domain.Known(true)
	} else {
		for _, issue := range v.Issues {
			if issue.GetField() == "naming" && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
				r.Facts.ColonyNaming = domain.Known(false)
			}
		}
	}
	if !hasIssue(v.Issues, "cooking") {
		benches := []CookingBench{}
		for _, bench := range v.Cooking {
			benches = append(benches, CookingBench{ID: bench.Bench.GetId(), Definition: bench.Bench.GetDefName(), Usable: optional(bench.Usable), Room: optional(bench.RoomId)})
		}
		r.CookingBenches = domain.Known(benches)
	}
	if food := v.GetFoodSupply().GetObserved(); food != nil {
		supply, err := DecodeFoodSupply(food)
		if err != nil {
			return ColonyProjection{}, err
		}
		r.FoodSupply = domain.Known(supply)
		r.Facts.FoodStorageUpkeep = policy.FoodStorageStocks(supply)
	}
	if forecast := v.GetForecast().GetObserved(); forecast != nil {
		combined, err := DecodeFoodSupply(forecast.CombinedFoodSupply)
		if err != nil {
			return ColonyProjection{}, err
		}
		r.CombinedFoodSupply = domain.Known(combined)
		if human, known := r.FoodSupply.Value(); known && len(human.Consumers) > 0 {
			selected := make([]policy.PawnID, 0, len(human.Consumers))
			for _, consumer := range human.Consumers {
				selected = append(selected, consumer.ID)
			}
			if food, err := policy.ForecastFood(combined, selected); err == nil {
				r.Facts.FoodDays = food.RunwayDays
				r.FoodAtRiskNutrition = domain.Known(food.AtRiskNutrition)
			}
		}
	}
	r.Facts.AnimalUpkeep.Food = r.CombinedFoodSupply
	if v.WorkerCount != nil {
		r.Workers = domain.Known(int(v.GetWorkerCount()))
	}
	if !hasIssue(v.Issues, "resources") {
		r.Facts.Wood = domain.Known(int64(0))
		rows := make([]policy.Amount, 0, len(v.Resources))
		complete := true
		stock := map[policy.Resource]int64{}
		for _, q := range v.Resources {
			if q.GetDefName() == "WoodLog" {
				r.Facts.Wood = optional(q.Units)
			}
			complete = complete && q.Units != nil
			rows = append(rows, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
			if q.Units != nil {
				stock[policy.Resource(q.GetDefName())] += q.GetUnits()
			}
		}
		if complete {
			r.Facts.Resources = domain.Known(rows)
		}
		r.Resources = domain.Known(stock)
	}
	if !hasIssue(v.Issues, "forbidden_supplies") {
		r.Facts.ForbiddenSupplies = domain.Known(len(v.ForbiddenSupplies) > 0)
		rows := make([]policy.StartingSupply, 0, len(v.ForbiddenSupplies))
		for _, row := range v.ForbiddenSupplies {
			rows = append(rows, policy.StartingSupply{Thing: row.GetId(), Definition: row.GetDefName(), Cell: domain.Cell{X: row.Position.GetX(), Z: row.Position.GetZ()}})
		}
		r.Facts.StartingSupplies = domain.Known(rows)
	}
	if loot := v.GetEventLoot().GetObserved(); loot != nil {
		rows := make([]policy.LootItem, 0, len(loot.Items))
		for _, row := range loot.Items {
			rows = append(rows, policy.LootItem{Supply: policy.StartingSupply{Thing: row.Item.GetId(), Definition: row.Item.GetDefName(), Cell: domain.Cell{X: row.Item.Position.GetX(), Z: row.Item.Position.GetZ()}}, Forbidden: row.GetForbidden(), SafeToHaul: row.GetSafeToHaul(), SafetyKnown: row.SafeToHaul != nil})
		}
		r.Facts.EventLoot = domain.Known(rows)
	}
	if planning := v.GetPlanning().GetObserved(); planning != nil {
		if planning.ZoneMapSnapshot != nil && !hasIssue(planning.Issues, "zone_map_snapshot") {
			r.ZoneMapToken = domain.Known(planning.ZoneMapSnapshot.GetToken())
		}
		for _, row := range planning.Definitions {
			d := PlanningDefinition{HarvestWork: optional(row.HarvestWork), RawPreferred: optional(row.RawPreferred), DietAllowed: optional(row.DietAllowed), RequiresPollution: optional(row.RequiresPollution), RequiresCleanSoil: optional(row.RequiresCleanSoil), Edible: optional(row.Edible), Name: row.Definition.GetDefName(), Stuff: optional(row.Stuff), Available: optional(row.Available), ConstructionSkill: optional(row.ConstructionSkill), NeedsPower: optional(row.NeedsPower), GrowDays: optional(row.GrowDays), FertilityMin: optional(row.FertilityMin), FertilitySensitivity: optional(row.FertilitySensitivity), HarvestNutrition: optional(row.HarvestNutrition), NutritionDemandPerDay: optional(row.NutritionDemandPerDay), GrowMinGlow: optional(row.GrowMinGlow), PowerW: optional(row.PowerW), GrowerFertility: optional(row.GrowerFertility), GlowRadius: optional(row.GlowRadius), SowTag: optional(row.SowTag), Terrain: optional(row.Terrain), Cleanliness: optional(row.Cleanliness), Beauty: optional(row.Beauty), Flammability: optional(row.Flammability), PathCost: optional(row.PathCost), Research: append([]string{}, row.ResearchPrerequisites...)}
			if row.GrowDays != nil {
				d.SowTags = domain.Known(append([]string{}, row.SowTags...))
			}
			if row.Size != nil {
				d.Size = domain.Known(policy.Bounds{Width: int32(row.Size.GetWidth()), Height: int32(row.Size.GetHeight())})
			}
			known := !hasIssue(row.Issues, "costs")
			var costs []policy.Amount
			for _, q := range row.Costs {
				if q.Units == nil {
					known = false
				}
				costs = append(costs, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
			}
			if known {
				d.Costs = domain.Known(costs)
			}
			r.Definitions = append(r.Definitions, d)
		}
		// An older native lists the planning window here; a current one
		// serves it through observations_get_cells and the routine bracket
		// fills Region and Cells from the store (#356).
		if region := planning.Cells.GetRegion(); region != nil && region.Minimum != nil && region.Maximum != nil {
			r.Region = policy.Rectangle{X: region.Minimum.GetX(), Z: region.Minimum.GetZ(), Width: region.Maximum.GetX() - region.Minimum.GetX() + 1, Height: region.Maximum.GetZ() - region.Minimum.GetZ() + 1}
			r.Cells, _ = bridge.PlanningCells(planning.Cells)
		}
	}
	if planning := v.GetPlanning().GetObserved(); planning != nil && planning.Environment != nil && !hasIssue(planning.Issues, "environment") {
		r.Environment = domain.Known(colonyEnvironment(planning.Environment))
	}
	if !hasIssue(v.Issues, "farms") {
		for _, farm := range v.Farms {
			if farm.ZoneId != nil && farm.Crop != nil {
				r.Farms = append(r.Farms, FarmZoneFact{ID: farm.GetZoneId(), Crop: farm.GetCrop(), UsableCells: optional(farm.UsableCells)})
			}
		}
	}
	r.FieldCrops = colonyFieldCrops(v, r.Definitions)
	r.FoodFields = colonyFoodFields(v, r.Definitions)
	r.FieldCapacityCrops = colonyFieldCrops(v, r.Definitions, true)
	r.Facts.Gear = colonyGear(v)
	return r, nil
}

func cellsOf(rows []*c.Cell) []domain.Cell {
	out := make([]domain.Cell, 0, len(rows))
	for _, row := range rows {
		out = append(out, domain.Cell{X: row.GetX(), Z: row.GetZ()})
	}
	return out
}

// colonyEnvironment decodes the native controlled-growing census. Rows keep
// their native order (ids ascending); every scalar stays unknown when the
// native side omitted it.
func colonyEnvironment(v *o.ControlledEnvironment) policy.ControlledEnvironment {
	e := policy.ControlledEnvironment{OutdoorTemperatureC: optional(v.OutdoorTemperatureC), Daylight: optional(v.Daylight)}
	for _, row := range v.Lights {
		ref := row.Building
		e.Lights = append(e.Lights, policy.GrowLight{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optional(row.RoomId), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: optional(row.PowerW), LitNow: optional(row.LitNow), GrowthCells: cellsOf(row.GrowthCells)})
	}
	for _, row := range v.Growers {
		ref := row.Building
		e.Growers = append(e.Growers, policy.PlantGrower{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optional(row.RoomId), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: optional(row.PowerW), Fertility: optional(row.Fertility), SowTag: optional(row.SowTag), Crop: optional(row.CropDefName), CanSow: optional(row.CanSow), Cells: cellsOf(row.PlantCells)})
	}
	for _, row := range v.Rooms {
		e.Rooms = append(e.Rooms, policy.GrowRoom{ID: row.GetRoomId(), TemperatureC: optional(row.TemperatureC), Cells: count(row.CellCount), OpenRoof: count(row.OpenRoofCount), Lit: count(row.LitCells), Proper: optional(row.ProperRoom), Outdoors: optional(row.PsychologicallyOutdoors)})
	}
	for _, row := range v.Networks {
		e.Networks = append(e.Networks, policy.PowerHeadroom{ID: row.GetId(), GenerationW: optional(row.GenerationW), SolarW: optional(row.SolarW), WindW: optional(row.WindW), ConsumptionW: optional(row.ConsumptionW), StoredWattDays: optional(row.StoredWattDays), CapacityWattDays: optional(row.CapacityWattDays), ActiveSource: optional(row.HasActiveSource)})
	}
	return e
}

func count(p *uint32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
