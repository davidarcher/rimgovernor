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
	Available                                                       domain.Fact[bool]
	ConstructionSkill                                               domain.Fact[int32]
	NeedsPower                                                      domain.Fact[bool]
	// MechCharger is true for a Building_MechCharger definition (#1688).
	MechCharger domain.Fact[bool]
	// Pollutes is true for a building with a pollution or wastepack comp
	// (CompToxifier, CompPolluteOverTime, CompWasteProducer); unknown when
	// the row did not say.
	Pollutes domain.Fact[bool]
	// Research names the ResearchProjectDefs the definition requires, as the
	// native definition declares them (unfinished ones make it unavailable).
	Research                                                                              []string
	Costs                                                                                 domain.Fact[[]policy.Amount]
	Size                                                                                  domain.Fact[policy.Bounds]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, NutritionDemandPerDay domain.Fact[float64]
	// Crop sow tags and minimum glow; grower sow tag and fertility; building
	// power draw and glow radius, as the native definition declares them.
	SowTags                                                           domain.Fact[[]string]
	GrowMinGlow, PowerW, GrowerFertility, GlowRadius, ExplosiveRadius domain.Fact[float64]
	SowTag                                                            domain.Fact[string]
	// Floor definition facts (issue #6 slice 4): Terrain marks a TerrainDef
	// and the stats are what a laid floor carries.
	Terrain                           domain.Fact[bool]
	Cleanliness, Beauty, Flammability domain.Fact[float64]
	PathCost                          domain.Fact[int32]
	// WorkToBuild is the native WorkToBuild stat (work ticks) for the row's
	// stuff (#950).
	WorkToBuild domain.Fact[float64]
	// Stuffed is whether the def is made from stuff (it has stuff
	// categories). A stuffed def has no single cost list: Costs is unknown
	// and StuffChoice prices it from StuffOptions.
	Stuffed bool
	// StuffOptions are every allowed stuff with its cost list and stat
	// values, ordered by defName; empty for a definition not made from
	// stuff, and for a stuffed one nothing is allowed to make it from.
	StuffOptions []StuffOption
	// RoomRoles are the room-role furniture roles the native catalog
	// assigns the definition (policy.FurnitureRole names), sorted.
	RoomRoles []string
}

// StuffOption is one material a stuffed definition may be built from.
type StuffOption struct {
	Stuff string
	Costs []policy.Amount
	// Value is the market value of Costs: what the cheapest criterion ranks by.
	Value float64
	// Stats are the stat values the game shows for the def made of this stuff
	// (the StuffChoice criteria); a stat the game does not show is absent.
	Stats map[string]float64
}
type ColonyProjection struct {
	DeepResources domain.Fact[DeepResources]
	Policies      domain.Fact[Policies]
	Biotech       domain.Fact[BiotechColony]
	Odyssey       domain.Fact[OdysseyColony]
	Anomaly       domain.Fact[AnomalyColony]
	// Containment is the containment cell's inputs (#1741), set by the routine reading.
	Containment         policy.ContainmentPlanning
	FoodFields          domain.Fact[[]policy.FoodField]
	FoodChannels        domain.Fact[FoodChannels]
	ProductionBenches   domain.Fact[[]policy.ProductionBench]
	ButcheringBenches   domain.Fact[[]CookingBench]
	FoodAtRiskNutrition domain.Fact[float64]
	CropClimate         policy.CropClimate

	PendingHunts domain.Fact[int]

	Acquisition                            domain.Fact[[]policy.AcquisitionSource]
	PendingFoodNutrition, PendingWoodUnits domain.Fact[float64]
	WorkPawns                              domain.Fact[[]policy.WorkPawn]
	// MechCatalog is the Biotech catalog's mech kinds (#1686); the zero value without Biotech.
	MechCatalog policy.MechCatalog
	// Mechs are the colony's mechanitors and mechs from the pawn table
	// (#1736); unknown without a table.
	Mechs              domain.Fact[policy.MechFleet]
	MeditateAvailable  domain.Fact[bool] // Meditate TimeAssignmentDef exists (#1313)
	FieldCrops         domain.Fact[[]policy.FieldCrop]
	FieldCapacityCrops domain.Fact[[]policy.FieldCrop]
	CookingBenches     domain.Fact[[]CookingBench]
	PowerPlanning      domain.Fact[policy.PowerTopology]
	// DefenseTurrets is every built turret gun in the power census with its
	// observed damage per second (#1188).
	DefenseTurrets domain.Fact[[]policy.DefenseTurretFacts]
	Rooms          domain.Fact[policy.RoomObservation]
	Identity       Identity
	// PlayerTechLevel is the player faction's native TechLevel name, the
	// faction's tech level.
	PlayerTechLevel domain.Fact[string]
	// BuildTier is the construction tier derived from finished research
	// with PlayerTechLevel as its floor (#604); unknown until a routine
	// reading served the research census.
	BuildTier domain.Fact[policy.BuildTier]
	// LayoutPlan is the persisted v2 layout (#783), served by the routine
	// review; unknown until one is derived. layoutAnchor reads it (#785).
	LayoutPlan domain.Fact[policy.LayoutPlan]
	// Royalty is the Empire ladder, permits and holdings (#1599); unknown
	// without Royalty or a royalty source.
	Royalty domain.Fact[policy.RoyaltyFacts]
	Facts   policy.RoutineFacts
	Workers domain.Fact[int]
	Bounds  policy.Bounds
	Center  domain.Cell
	// Region is the observed planning window; cells absent inside it are
	// fogged, cells outside it were never read.
	Region policy.Rectangle
	Cells  []policy.SiteCell
	// Window is the window's provenance when a PlanningWindowSource served
	// it (Source set); empty when the reply listed the cells itself.
	Window facts.Held[PlanningCells]
	Zones  facts.Held[bridge.ZonesRead]
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
	// Threat is the census's wealth split and raid points (#395); every
	// reading is unknown under a native build without the section.
	Threat bridge.ColonyThreat
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

// StockedStuff is the first of the definition's stuff options whose cost
// list the colony stock covers, and how many of the definition that stock
// builds. None is ok false, as is an unknown stock census.
func (r ColonyProjection) StockedStuff(name string) (string, int64, bool) {
	stock, known := r.Resources.Value()
	if !known {
		return "", 0, false
	}
	for _, d := range r.Definitions {
		if d.Name != name {
			continue
		}
		for _, option := range d.StuffOptions {
			count := int64(-1)
			for _, cost := range option.Costs {
				if cost.Count <= 0 {
					continue
				}
				if n := stock[cost.Resource] / cost.Count; count < 0 || n < count {
					count = n
				}
			}
			if count > 0 {
				return option.Stuff, count, true
			}
		}
	}
	return "", 0, false
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
	// AutoRefuel is a refuelable bench's auto-refuel toggle (#1180),
	// unknown for a bench without one.
	AutoRefuel domain.Fact[bool]
}

// resolved reports whether buildings holds the building every row refers
// to (#1343).
func resolved[R any, F bridge.Reference](buildings bridge.Buildings, rows []R, ref func(R) F) bool {
	for _, row := range rows {
		if _, ok := buildings.Row(ref(row)); !ok {
			return false
		}
	}
	return true
}

// entities resolves a reference to its row's head: bridge.Buildings or
// bridge.Tables.
type entities interface {
	Entity(bridge.Reference) *o.EntityRef
}

// headed reports whether tables hold the head every row refers to: the
// def and position a family reads from the table (#1342).
func headed[R any, F bridge.Reference](tables entities, rows []R, ref func(R) F) bool {
	for _, row := range rows {
		if tables.Entity(ref(row)) == nil {
			return false
		}
	}
	return true
}

func optional[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}

// optionalRef is r's id, unknown without a reference.
func optionalRef(r *c.Ref) domain.Fact[string] {
	if r == nil {
		return domain.Unknown[string]()
	}
	return optional(r.Id)
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
//
// tables are the frame's keyed tables every building and pawn reference
// resolves against (#1343); a family with a reference they do not hold is
// unknown until a later frame.
func DecodeColony(reply *o.ColonyFactsReply, expected Identity, tables bridge.Tables) (ColonyProjection, error) {
	buildings := tables.Buildings
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
	// The colony facts row may come from the step's fact cache at any
	// earlier tick (#306); only another generation changes its world.
	if generation, known := expected.NativeGeneration.Value(); known {
		if actual, observed := identity.NativeGeneration.Value(); observed && actual != generation {
			return ColonyProjection{}, ErrChanged
		}
	}
	r := ColonyProjection{Identity: identity, Bounds: policy.Bounds{Width: int32(v.MapSize.GetWidth()), Height: int32(v.MapSize.GetHeight())}, Center: domain.Cell{X: v.Center.GetX(), Z: v.Center.GetZ()}}
	r.PlayerTechLevel = optional(v.PlayerTechLevel)
	r.BuildTier = domain.Unknown[policy.BuildTier]()
	r.Threat = bridge.ProjectColonyThreat(v)
	r.FoodChannels = colonyFoodChannels(v.FoodChannels)
	r.DeepResources = colonyDeepResources(v.DeepResources)
	r.Policies = ColonyPolicies(v.Policies)
	if policies, known := r.Policies.Value(); known {
		policies.Books = tables.Catalog.Books()
		policies.Foods = tables.Catalog.Foods()
		r.Policies = domain.Known(policies)
	}
	r.Biotech = colonyBiotech(v.Biotech)
	r.Odyssey = colonyOdyssey(v.Odyssey)
	r.Anomaly = colonyAnomaly(v.Anomaly)
	r.Facts = policy.RoutineFacts{Colonists: countFact(v.ColonistCount), BedCapacity: countFact(v.BedCapacity), IndoorCapacity: countFact(v.IndoorSleepingCapacity), SleepingMin: optional(v.SleepingTemperatureMinC), SleepingMax: optional(v.SleepingTemperatureMaxC), OutdoorTemperature: optional(v.OutdoorTemperatureC)}
	r.Facts.MapBounds = domain.Known(r.Bounds)
	r.Facts.ShelterArea = shelterArea(r.Policies)
	r.Facts.NoKillboxArea = allowedAreaID(r.Policies, policy.NoKillboxAreaLabel)
	r.Facts.VetRoom.Area = allowedAreaID(r.Policies, policy.VetRoomAreaLabel)
	r.Facts.RaidPoints = bridge.ProjectColonyThreat(v).RaidPoints
	threat := bridge.ProjectColonyThreat(v)
	items, itemsKnown := threat.WealthItems.Value()
	wealthBuildings, buildingsKnown := threat.WealthBuildings.Value()
	pawns, pawnsKnown := threat.WealthPawns.Value()
	total, totalKnown := threat.WealthTotal.Value()
	if itemsKnown && buildingsKnown && pawnsKnown && totalKnown {
		r.Facts.Wealth = domain.Known(policy.WealthFacts{Items: items, Buildings: wealthBuildings, Pawns: pawns, Total: total})
	}
	if channels, known := r.FoodChannels.Value(); known {
		r.Facts.PenGrazing = domain.Known(channels.Grazing)
	}
	if development := v.GetDevelopment().GetObserved(); development != nil && resolved(buildings, development.Power, (*o.DevelopmentPower).GetBuilding) {
		power := make([]policy.PowerBuilding, 0, len(development.Power))
		topology := policy.PowerTopology{}
		geometryKnown := true
		turrets := []policy.DefenseTurretFacts{}
		if !hasIssue(v.Issues, "environment") {
			blackout, eclipse := false, false
			for _, condition := range v.Environment {
				blackout = blackout || condition.GetDefName() == "SolarFlare"
				eclipse = eclipse || condition.GetDefName() == "Eclipse"
			}
			topology.Blackout, topology.Eclipse = domain.Known(blackout), domain.Known(eclipse)
		}
		for _, row := range development.Power {
			b, _ := buildings.Row(row.Building)
			s := b.Service
			// Whether rain shorts the building is its def's, not the frame's (#1733).
			rainVulnerable := domain.Unknown[bool]()
			if def := b.GetBuilding().DefName; def != nil && tables.Catalog != nil {
				vulnerable, err := tables.Catalog.RainVulnerable(*def)
				if err != nil {
					return ColonyProjection{}, err
				}
				rainVulnerable = domain.Known(vulnerable)
			}
			power = append(power, policy.PowerBuilding{BaseW: optional(row.BaseW), OutputW: optional(s.PowerOutputW), Powered: optional(s.PowerOn), Connected: optional(s.Connected), Network: optional(s.PowerNetId), Forbidden: optional(b.Settings.Forbidden), SwitchedOn: optional(s.SwitchedOn),
				Fuel: optional(s.Fuel), TargetFuel: optional(s.TargetFuel), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: append([]string(nil), s.AllowedFuelDefs...),
				Stored: optional(row.StoredWattDays), Capacity: optional(row.CapacityWattDays), RainVulnerable: rainVulnerable, Roofed: optional(row.Roofed), TurretDPS: optional(row.TurretDps)})
			ref := b.GetBuilding()
			site := policy.PowerSite{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, PowerBuilding: power[len(power)-1], Occupied: bridge.RectCells(b.Occupied)}
			geometryKnown = geometryKnown && ref.DefName != nil && ref.Position != nil && len(site.Occupied) > 0
			topology.Buildings = append(topology.Buildings, site)
			if _, gun := site.TurretDPS.Value(); gun {
				turrets = append(turrets, policy.DefenseTurretFacts{ID: site.ID, Definition: site.Definition, Cell: site.Cell, Powered: site.Powered, DPS: site.TurretDPS})
			}
		}
		for _, row := range development.Furniture {
			conduit := buildings.Entity(row.Building)
			if conduit.GetPosition() == nil {
				geometryKnown = false
				continue
			}
			topology.Conduits = append(topology.Conduits, domain.Cell{X: conduit.Position.GetX(), Z: conduit.Position.GetZ()})
			if conduit.GetDefName() == "PowerConduit" {
				topology.UnsafeConduits = append(topology.UnsafeConduits, topology.Conduits[len(topology.Conduits)-1])
			}
		}
		for _, row := range development.Networks {
			topology.Networks = append(topology.Networks, policy.PowerNetworkFact{ID: row.GetId(), GenerationW: optional(row.GenerationW), ConsumptionW: optional(row.ConsumptionW), StoredWD: optional(row.StoredWattDays), CapacityWD: optional(row.CapacityWattDays)})
		}
		for _, row := range development.Geysers {
			ref := tables.Entity(row.GetGeyser())
			if ref.GetPosition() == nil {
				geometryKnown = false
				continue
			}
			geyser := policy.PowerGeyser{ID: ref.GetId(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Occupied: row.GetOccupied()}
			for _, c := range row.Cells {
				geyser.Cells = append(geyser.Cells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			topology.Geysers = append(topology.Geysers, geyser)
		}
		if geometryKnown {
			r.PowerPlanning = domain.Known(topology)
		}
		r.DefenseTurrets = domain.Known(turrets)
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
	colonyAcquisition(v, tables, &r)
	colonyProduction(v, &r.Facts)
	var benchesErr error
	if r.ProductionBenches, benchesErr = colonyProductionBenches(v, buildings, tables.Catalog); benchesErr != nil {
		return ColonyProjection{}, benchesErr
	}
	r.Facts.TradeMealIngredients = policy.TradeMealIngredients(r.ProductionBenches)
	if !hasIssue(v.Issues, "butchering") && headed(buildings, v.Butchering, (*o.ButcheringFacts).GetBench) {
		benches := []CookingBench{}
		for _, b := range v.Butchering {
			benches = append(benches, CookingBench{ID: b.Bench.GetId(), Definition: buildings.Entity(b.Bench).GetDefName(), Usable: optional(b.Usable), Room: optionalRef(b.Room)})
		}
		standing := []policy.ButcherBench{}
		for _, b := range benches {
			standing = append(standing, policy.ButcherBench{ID: b.ID, Definition: b.Definition, Room: b.Room})
		}
		r.Facts.ButcherBenches = domain.Known(standing)
		r.ButcheringBenches = domain.Known(benches)
	}
	colonyDisaster(v, &r.Facts, buildings)
	var comfortErr error
	if r.Facts.Comfort, comfortErr = colonyComfort(v, tables.Catalog); comfortErr != nil {
		return ColonyProjection{}, comfortErr
	}
	r.Facts.BasicComfort = r.Facts.Comfort
	r.Facts.HomeCoverage = colonyHomeCoverage(v)
	r.Facts.StoneStructures = colonyStoneStructures(v, buildings)
	r.Facts.Sleeping = colonySleeping(v, buildings)
	r.Facts.AnimalUpkeep.Animals = mergeHerdFoodFacts(colonyAnimals(v, tables.Pawns), r.FoodChannels)
	r.Facts.AnimalUpkeep.WildAnimals = colonyWildAnimals(v, tables.Pawns)
	r.Facts.Waste = colonyWaste(v, tables)
	r.Facts.Blight = colonyBlight(v, tables)
	r.Facts.Pollution = colonyPollution(r.Biotech)
	var upkeepErr, medicalErr error
	if r.Facts.Upkeep, upkeepErr = colonyUpkeep(v, tables); upkeepErr != nil {
		return ColonyProjection{}, upkeepErr
	}
	if r.Facts.MedicalReserve, medicalErr = ColonyMedicalReserve(v, tables); medicalErr != nil {
		return ColonyProjection{}, medicalErr
	}
	// The dialog section is present exactly while a force-pausing choice
	// dialog is open (#156); native omits it otherwise.
	r.Facts.ChoiceDialog = domain.Known(v.Dialog != nil)
	letters := make([]policy.JoinerLetterOffer, 0, len(v.JoinerLetters))
	for _, row := range v.JoinerLetters {
		letters = append(letters, policy.JoinerLetterOffer{ID: row.GetLetterId(), Token: row.GetSnapshotToken(), Pawn: domain.PawnID(row.GetPawnId()), Expires: domain.Tick(row.GetExpiresTick()), Label: row.GetAcceptLabel(), CanAccept: row.GetCanAccept(), CreepJoiner: row.GetCreepjoiner()})
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
	if !hasIssue(v.Issues, "cooking") && headed(buildings, v.Cooking, (*o.CookingFacts).GetBench) {
		benches := []CookingBench{}
		for _, bench := range v.Cooking {
			benches = append(benches, CookingBench{ID: bench.Bench.GetId(), Definition: buildings.Entity(bench.Bench).GetDefName(), Usable: optional(bench.Usable), Room: optionalRef(bench.Room), AutoRefuel: optional(bench.AutoRefuel)})
		}
		r.CookingBenches = domain.Known(benches)
	}
	// A food supply whose stock the things table misses stays unknown
	// until a later frame (#1343).
	if food := v.GetFoodSupply().GetObserved(); food != nil {
		supply, known, err := DecodeFoodSupply(food, tables.Things, tables.Catalog)
		if err != nil {
			return ColonyProjection{}, err
		}
		if known {
			r.FoodSupply = domain.Known(supply)
			r.Facts.FoodStorageUpkeep = policy.FoodStorageStocks(supply, float64(tables.Catalog.Constants.FullRotRateC))
		}
	}
	if forecast := v.GetForecast().GetObserved(); forecast != nil {
		combined, resolved, err := DecodeFoodSupply(forecast.CombinedFoodSupply, tables.Things, tables.Catalog)
		if err != nil {
			return ColonyProjection{}, err
		}
		if resolved {
			r.CombinedFoodSupply = domain.Known(combined)
		}
		if human, known := r.FoodSupply.Value(); resolved && known && len(human.Consumers) > 0 {
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
	if !hasIssue(v.Issues, "forbidden_supplies") && headed(tables, v.ForbiddenSupplies, func(r *c.Ref) *c.Ref { return r }) {
		r.Facts.ForbiddenSupplies = domain.Known(len(v.ForbiddenSupplies) > 0)
		rows := make([]policy.StartingSupply, 0, len(v.ForbiddenSupplies))
		for _, row := range v.ForbiddenSupplies {
			head := tables.Entity(row)
			rows = append(rows, policy.StartingSupply{Thing: row.GetId(), Definition: head.GetDefName(), Cell: domain.Cell{X: head.GetPosition().GetX(), Z: head.GetPosition().GetZ()}})
		}
		r.Facts.StartingSupplies = domain.Known(rows)
	}
	if loot := v.GetEventLoot().GetObserved(); loot != nil && headed(tables, loot.Items, (*o.LootItem).GetItem) {
		rows := make([]policy.LootItem, 0, len(loot.Items))
		for _, row := range loot.Items {
			head := tables.Entity(row.Item)
			item := policy.LootItem{Supply: policy.StartingSupply{Thing: row.Item.GetId(), Definition: head.GetDefName(), Cell: domain.Cell{X: head.GetPosition().GetX(), Z: head.GetPosition().GetZ()}}, Forbidden: row.GetForbidden(), SafeToHaul: row.GetSafeToHaul(), SafetyKnown: row.SafeToHaul != nil, Count: row.GetCount()}
			if row.PathLength != nil {
				item.PathLength = domain.Known(row.GetPathLength())
			}
			if row.StorageHeadroom != nil {
				item.StorageHeadroom = domain.Known(row.GetStorageHeadroom())
			}
			rows = append(rows, item)
		}
		r.Facts.EventLoot = domain.Known(rows)
		if loot.FreeHaulers != nil {
			r.Facts.LootReadiness.FreeHaulers = domain.Known(loot.GetFreeHaulers())
		}
		if loot.StorytellerQuiet != nil {
			r.Facts.LootReadiness.StorytellerQuiet = domain.Known(loot.GetStorytellerQuiet())
		}
	}
	if planning := v.GetPlanning().GetObserved(); planning != nil && planning.Environment != nil && !hasIssue(planning.Issues, "environment") && headed(buildings, planning.Environment.Lights, (*o.GrowLight).GetBuilding) && headed(buildings, planning.Environment.Growers, (*o.PlantGrower).GetBuilding) {
		r.Environment = domain.Known(colonyEnvironment(planning.Environment, v.OutdoorTemperatureC, buildings))
	}
	// r.Facts.Gear needs the catalog: ObserveColony fills it (#1732).
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
func colonyEnvironment(v *o.ControlledEnvironment, outdoor *float64, buildings bridge.Buildings) policy.ControlledEnvironment {
	e := policy.ControlledEnvironment{OutdoorTemperatureC: optional(outdoor), Daylight: optional(v.Daylight), Weather: optional(v.Weather)}
	for _, row := range v.Lights {
		ref := buildings.Entity(row.Building)
		e.Lights = append(e.Lights, policy.GrowLight{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optionalRef(row.Room), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: optional(row.PowerW), LitNow: optional(row.LitNow), GrowthCells: cellsOf(row.GrowthCells)})
	}
	for _, row := range v.Growers {
		ref := buildings.Entity(row.Building)
		e.Growers = append(e.Growers, policy.PlantGrower{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optionalRef(row.Room), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: optional(row.PowerW), Fertility: optional(row.Fertility), SowTag: optional(row.SowTag), Crop: optional(row.CropDefName), CanSow: optional(row.CanSow), Cells: cellsOf(row.PlantCells)})
	}
	for _, row := range v.Rooms {
		e.Rooms = append(e.Rooms, policy.GrowRoom{ID: row.GetRoom().GetId(), TemperatureC: optional(row.TemperatureC), Cells: count(row.CellCount), OpenRoof: count(row.OpenRoofCount), Lit: count(row.LitCells), Proper: optional(row.ProperRoom), Outdoors: optional(row.PsychologicallyOutdoors)})
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
