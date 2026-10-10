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
	// MechCharger is true for a Building_MechCharger definition.
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
	// HarvestRotDays and HarvestPerishable are the harvested item's rot facts.
	HarvestRotDays    domain.Fact[float64]
	HarvestPerishable domain.Fact[bool]
	// HarvestedThingDef and HarvestYield are what one harvest of the plant
	// yields; SowMinSkill is the sowing skill floor and
	// HarvestDestroysPlant whether a harvest removes the plant (a tree is felled,
	// a rice stand is cut). Unknown for a plant that names no product.
	HarvestedThingDef    domain.Fact[string]
	HarvestYield         domain.Fact[float64]
	SowMinSkill          domain.Fact[int32]
	HarvestDestroysPlant domain.Fact[bool]
	// BlockAdjacentSow, MustBeWildToSow, HarvestMinGrowth, SowWork and
	// WildBiomes are the sowing facts a tree plantation prices: the
	// native sower leaves the cells beside a sown tree empty, the game offers
	// only wild species to sow, a tree is harvestable from HarvestMinGrowth,
	// one sowing costs SowWork ticks, and WildBiomes are the biome defNames the
	// plant grows wild in.
	BlockAdjacentSow, MustBeWildToSow domain.Fact[bool]
	HarvestMinGrowth, SowWork         domain.Fact[float64]
	WildBiomes                        domain.Fact[[]string]
	// Crop sow tags and minimum glow; grower sow tag and fertility; building
	// power draw and glow radius, as the native definition declares them.
	SowTags                                                           domain.Fact[[]string]
	GrowMinGlow, PowerW, GrowerFertility, GlowRadius, ExplosiveRadius domain.Fact[float64]
	// The crop's growth temperature range (PlantProperties).
	MinGrowthTemperature, MinOptimalGrowthTemperature, MaxOptimalGrowthTemperature, MaxGrowthTemperature domain.Fact[float64]
	SowTag                                                                                               domain.Fact[string]
	// Floor definition facts: Terrain marks a TerrainDef
	// and the stats are what a laid floor carries.
	Terrain                           domain.Fact[bool]
	Cleanliness, Beauty, Flammability domain.Fact[float64]
	PathCost                          domain.Fact[int32]
	// WorkToBuild is the native WorkToBuild stat (work ticks) for the row's
	// stuff.
	WorkToBuild domain.Fact[float64]
	// FloorTags are a TerrainDef's tags (the throne floor requirement).
	FloorTags []string
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
	// Common is whether the game generates the stuff as an ordinary material
	// (StuffProperties.commonality above zero). Bioferrite is not: it is only
	// ever made by an Anomaly colony, so a def is never planned from it while
	// an ordinary stuff is allowed.
	Common bool
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
	// RoyaltyColony is the Royalty colony section; unknown without Royalty or when the read failed.
	RoyaltyColony domain.Fact[policy.RoyaltyColony]
	// Isolation is the creepjoiner isolation room's inputs, set by the rounds.
	Isolation policy.IsolationPlanning
	// Strangers is what the tomb reads to stage stranger corpses, set by
	// the rounds; the zero value stages none.
	Strangers policy.StrangerTomb
	// Shapes are the catalog's piece shapes and the furniture its rules choose
	// (DefinitionCatalog.PieceShapes), set when a catalog is read; the zero
	// value without one.
	Shapes              policy.PieceShapes
	FoodFields          domain.Fact[[]policy.FoodField]
	FoodChannels        domain.Fact[FoodChannels]
	ProductionBenches   domain.Fact[[]policy.ProductionBench]
	ButcheringBenches   domain.Fact[[]CookingBench]
	FoodAtRiskNutrition domain.Fact[float64]
	CropClimate         policy.CropClimate
	// ColdMap is whether the seasonal outdoor temperature curve dips below
	// freezing (policy.ColdMapCurve); unknown without the curve.
	ColdMap domain.Fact[bool]
	// HotMap is whether the curve peaks above HotEnter (policy.HotMapCurve); unknown without the curve.
	HotMap domain.Fact[bool]

	PendingHunts domain.Fact[int]
	// DeliveryLedger is native's cumulative delivery counters; unknown when the census is missing or malformed.
	DeliveryLedger domain.Fact[DeliveryLedger]

	Acquisition domain.Fact[[]policy.AcquisitionSource]
	// HuntHolds are the hunt rows policy.HuntGate holds, with the gate that failed.
	HuntHolds                              []policy.HuntHold `json:",omitempty"`
	PendingFoodNutrition, PendingWoodUnits domain.Fact[float64]
	WorkPawns                              domain.Fact[[]policy.WorkPawn]
	// MechCatalog is the Biotech catalog's mech kinds; the zero value without Biotech.
	MechCatalog policy.MechCatalog
	// Mechs are the colony's mechanitors and mechs from the pawn table;
	// unknown without a table.
	Mechs              domain.Fact[policy.MechFleet]
	MeditateAvailable  domain.Fact[bool] // Meditate TimeAssignmentDef exists
	FieldCrops         domain.Fact[[]policy.FieldCrop]
	FieldCapacityCrops domain.Fact[[]policy.FieldCrop]
	CookingBenches     domain.Fact[[]CookingBench]
	PowerPlanning      domain.Fact[policy.PowerTopology]
	// PowerSources and PowerBattery are the catalog's producer delivery
	// profiles and the storage of its battery def (DefinitionCatalog.PowerSources,
	// PowerBattery); the power planner refuses a projection without them.
	PowerSources map[string]policy.PowerSourceProfile
	PowerBattery policy.PowerBattery
	// LightTerrains are the terrain defs affording Light (DefinitionCatalog.LightTerrains).
	LightTerrains policy.LightTerrains
	// RoofSupport is the game's roof support radius (the catalog constant
	// roof_max_support_distance).
	RoofSupport float64
	// Impressiveness are the game's room impressiveness stage thresholds
	// (the Impressiveness RoomStatDef's score stages).
	Impressiveness policy.ImpressivenessLevels
	// DefenseTurrets is every built turret gun in the power census with its
	// observed damage per second.
	DefenseTurrets domain.Fact[[]policy.DefenseTurretFacts]
	Rooms          domain.Fact[policy.RoomObservation]
	Identity       Identity
	// PlayerTechLevel is the player faction's native TechLevel name, the
	// faction's tech level.
	PlayerTechLevel domain.Fact[string]
	// Packable is the catalog's building defs that pack (minifiedDef);
	// empty without a catalog.
	Packable map[string]bool
	// TechTier is the construction tier derived from finished research
	// with PlayerTechLevel as its floor; unknown until a routine
	// reading served the research census.
	TechTier domain.Fact[policy.TechTier]
	// LayoutPlan is the persisted v2 layout, served by the routine
	// review; unknown until one is derived. layoutAnchor reads it.
	LayoutPlan domain.Fact[policy.LayoutPlan]
	// Royalty is the Empire ladder, permits and holdings; unknown
	// without Royalty or a royalty source.
	Royalty domain.Fact[policy.RoyaltyFacts]
	Facts   policy.RoundsFacts
	// BedPrice prices a bed by (def, stuff) from the catalog's MarketValue
	// rows; nil without a catalog.
	BedPrice policy.BedPrice
	Workers  domain.Fact[int]
	Bounds   policy.Bounds
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
	// Threat is the census's wealth split and raid points; every
	// reading is unknown under a native build without the section.
	Threat bridge.ColonyThreat
}

// ResourceStock reports the accessible stock of one definition, unknown when
// the census itself is unknown.
// Center is the middle of the layout plan's core: unknown until a plan
// exists, so nothing anchors on where the colonists happen to stand.
func (r ColonyProjection) Center() domain.Fact[domain.Cell] {
	if plan, ok := r.LayoutPlan.Value(); ok {
		if center, ok := plan.Center(); ok {
			return domain.Known(center)
		}
	}
	return domain.Unknown[domain.Cell]()
}

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

// StuffFunded reports whether the colony stock, net of holds, covers one allowed
// stuff's cost list for one of the definition (the next sarcophagus).
// An unknown stock census is not funded.
func (r ColonyProjection) StuffFunded(name string, holds map[policy.Resource]int64) bool {
	stock, known := r.Resources.Value()
	if !known {
		return false
	}
	for _, d := range r.Definitions {
		if d.Name != name {
			continue
		}
		for _, option := range d.StuffOptions {
			covered := len(option.Costs) > 0
			for _, cost := range option.Costs {
				if cost.Count > 0 && stock[cost.Resource]-holds[cost.Resource] < cost.Count {
					covered = false
				}
			}
			if covered {
				return true
			}
		}
	}
	return false
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
// native availability, observed fuel stock and catalog delivery profile.
func (r ColonyProjection) GeneratorOptions() ([]policy.GeneratorOption, error) {
	available := map[string]domain.Fact[bool]{}
	for _, d := range r.Definitions {
		available[d.Name] = d.Available
	}
	return policy.DefaultGeneratorOptions(func(name string) domain.Fact[bool] {
		if fact, ok := available[name]; ok {
			return fact
		}
		return domain.Unknown[bool]()
	}, r.ResourceStock, r.PowerSources)
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
	// AutoRefuel is a refuelable bench's auto-refuel toggle,
	// unknown for a bench without one.
	AutoRefuel domain.Fact[bool]
}

// resolved reports whether buildings holds the building every row refers
// to.
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
// def and position a family reads from the table.
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
// resolves against; a family with a reference they do not hold is
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
	// earlier tick; only another generation changes its world.
	if generation, known := expected.NativeGeneration.Value(); known {
		if actual, observed := identity.NativeGeneration.Value(); observed && actual != generation {
			return ColonyProjection{}, ErrChanged
		}
	}
	r := ColonyProjection{Identity: identity, Bounds: policy.Bounds{Width: int32(v.MapSize.GetWidth()), Height: int32(v.MapSize.GetHeight())}}
	r.PlayerTechLevel = optional(v.PlayerTechLevel)
	r.TechTier = domain.Unknown[policy.TechTier]()
	r.Threat = bridge.ProjectColonyThreat(v)
	// The race rows are static for a load and come from the catalog; an
	// unbuildable race table fails the reading.
	races, racesErr := tables.Catalog.AnimalRaces()
	if racesErr != nil {
		return ColonyProjection{}, racesErr
	}
	r.FoodChannels = colonyFoodChannels(v.FoodChannels, tables.Pawns, races)
	r.DeliveryLedger = colonyDeliveryLedger(v.DeliveryLedger, v.Context.GetTick())
	r.DeepResources = colonyDeepResources(v.DeepResources, tables.Catalog)
	r.Policies = ColonyPolicies(v.Policies)
	if policies, known := r.Policies.Value(); known {
		policies.Books = tables.Catalog.Books()
		foods, err := tables.Catalog.Foods()
		if err != nil {
			return ColonyProjection{}, err
		}
		policies.Foods = foods
		if v.Biome != nil {
			diseases, err := tables.Catalog.BiomeDiseases(v.GetBiome())
			if err != nil {
				return ColonyProjection{}, err
			}
			policies.BiomeDiseases = diseases
		}
		r.Policies = domain.Known(policies)
	}
	conditions, conditionsKnown, err := colonyConditions(v, tables.Catalog)
	if err != nil {
		return ColonyProjection{}, err
	}
	if tables.Catalog != nil {
		game, err := tables.Catalog.GameConstants()
		if err != nil {
			return ColonyProjection{}, err
		}
		r.RoofSupport = float64(game.GetRoofCollapseUtility().GetRoofMaxSupportDistance())
		r.BedPrice = marketBedPrice(tables.Catalog)
		r.Packable = tables.Catalog.Packable()
		r.LightTerrains = tables.Catalog.LightTerrains()
		if r.Impressiveness, err = tables.Catalog.ImpressivenessLevels(); err != nil {
			return ColonyProjection{}, err
		}
		if r.PowerSources, err = tables.Catalog.PowerSources(); err != nil {
			return ColonyProjection{}, err
		}
		if tables.Catalog.ThingDef(policy.BatteryDefinition) != nil {
			if r.PowerBattery, err = tables.Catalog.PowerBattery(policy.BatteryDefinition); err != nil {
				return ColonyProjection{}, err
			}
		}
	}
	// The biome's permanent darkness is its map conditions', read
	// only where something decides on it (sowing climate, a lighting census):
	// there a frame without a biome read or a catalog fails, never counts as lit.
	var outdoorsDark domain.Fact[bool]
	r.Biotech = colonyBiotech(v.Biotech)
	r.Odyssey = colonyOdyssey(v.Odyssey)
	r.Anomaly = colonyAnomaly(v.Anomaly)
	if r.RoyaltyColony, err = colonyRoyalty(v.Royalty); err != nil {
		return ColonyProjection{}, err
	}
	r.Facts = policy.RoundsFacts{Colonists: countFact(v.ColonistCount), BedCapacity: countFact(v.BedCapacity), IndoorCapacity: countFact(v.IndoorSleepingCapacity), SleepingMin: optional(v.SleepingTemperatureMinC), SleepingMax: optional(v.SleepingTemperatureMaxC), OutdoorTemperature: optional(v.OutdoorTemperatureC)}
	r.Facts.MapBounds = domain.Known(r.Bounds)
	r.Facts.ShelterArea = shelterArea(r.Policies)
	r.Facts.NoDangerArea = allowedAreaID(r.Policies, policy.NoDangerAreaLabel)
	r.Facts.VetRoom.Area = allowedAreaID(r.Policies, policy.VetRoomAreaLabel)
	r.Facts.BarnArea = allowedAreaID(r.Policies, policy.BarnAreaLabel)
	r.Facts.CompanionArea = allowedAreaID(r.Policies, policy.CompanionAreaLabel)
	r.Facts.WildArea = allowedAreaID(r.Policies, policy.WildAreaLabel)
	r.Facts.IsolationArea = allowedAreaID(r.Policies, policy.IsolationAreaLabel)
	r.Facts.RaidPoints = bridge.ProjectColonyThreat(v).RaidPoints
	r.Facts.Monolith = monolithFacts(r.Anomaly)
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
		fuel := []policy.FuelConsumer{}
		if conditionsKnown {
			blackout, eclipse := false, false
			for _, condition := range conditions {
				blackout = blackout || condition.DisablesPower
				eclipse = eclipse || condition.Definition == "Eclipse"
			}
			topology.Blackout, topology.Eclipse = domain.Known(blackout), domain.Known(eclipse)
		}
		for _, row := range development.Power {
			b, _ := buildings.Row(row.Building)
			s := b.Service
			// Whether rain shorts the building is its def's, not the frame's.
			rainVulnerable := domain.Unknown[bool]()
			if def := b.GetBuilding().DefName; def != nil && tables.Catalog != nil {
				vulnerable, err := tables.Catalog.RainVulnerable(*def)
				if err != nil {
					return ColonyProjection{}, err
				}
				rainVulnerable = domain.Known(vulnerable)
			}
			var fuels []string
			if def := b.GetBuilding().DefName; def != nil {
				if fuels, err = tables.Catalog.RefuelFuels(*def); err != nil {
					return ColonyProjection{}, err
				}
			}
			baseW := domain.Unknown[float64]()
			if def := b.GetBuilding().DefName; def != nil && tables.Catalog != nil {
				if baseW, err = tables.Catalog.PowerBaseW(*def, tables.FinishedResearch); err != nil {
					return ColonyProjection{}, err
				}
			}
			capacity := domain.Unknown[float64]()
			if def := b.GetBuilding().DefName; def != nil && tables.Catalog != nil {
				if capacity, err = tables.Catalog.BatteryCapacityWD(*def); err != nil {
					return ColonyProjection{}, err
				}
			}
			power = append(power, policy.PowerBuilding{BaseW: baseW, OutputW: optional(s.PowerOutputW), Powered: optional(s.PowerOn), Connected: optional(s.Connected), Network: optional(s.PowerNetId), Forbidden: optional(b.Settings.Forbidden), SwitchedOn: optional(s.SwitchedOn),
				Fuel: optional(s.Fuel), TargetFuel: optional(s.TargetFuel), OutOfFuel: optional(s.OutOfFuel), BrokenDown: optional(s.BrokenDown), FuelDefinitions: fuels,
				Stored: optional(row.StoredWattDays), Capacity: capacity, RainVulnerable: rainVulnerable, Roofed: optional(row.Roofed), TurretDPS: optional(row.TurretDps)})
			ref := b.GetBuilding()
			consumer, refuelable, err := fuelConsumer(tables.Catalog, ref.GetId(), ref.GetDefName(), power[len(power)-1])
			if err != nil {
				return ColonyProjection{}, err
			}
			if refuelable {
				fuel = append(fuel, consumer)
			}
			site := policy.PowerSite{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, PowerBuilding: power[len(power)-1], Occupied: bridge.RectCells(b.Occupied)}
			geometryKnown = geometryKnown && ref.DefName != nil && ref.Position != nil && len(site.Occupied) > 0
			topology.Buildings = append(topology.Buildings, site)
			if _, gun := site.TurretDPS.Value(); gun {
				turrets = append(turrets, policy.DefenseTurretFacts{ID: site.ID, Definition: site.Definition, Cell: site.Cell, Powered: site.Powered, DPS: site.TurretDPS})
			}
		}
		// A conduit is a built plain building of a def whose power comp only
		// transmits: the def rows say which, the building table says where.
		for _, row := range buildings.Sorted() {
			if row.GetStatus() != o.BuildingStatus_BUILDING_STATUS_BUILT || row.GetBuilding().DefName == nil || tables.Catalog == nil {
				continue
			}
			conduit, err := tables.Catalog.PlainConduit(row.GetBuilding().GetDefName())
			if err != nil {
				return ColonyProjection{}, err
			}
			if !conduit {
				continue
			}
			if row.GetBuilding().GetPosition() == nil {
				geometryKnown = false
				continue
			}
			topology.Conduits = append(topology.Conduits, domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()})
			if row.Building.GetDefName() == "PowerConduit" {
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
		r.Facts.Fuel = domain.Known(fuel)
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
		var err error
		if outdoorsDark, err = colonyOutdoorsDark(v, tables.Catalog); err != nil {
			return ColonyProjection{}, err
		}
		r.CropClimate = policy.CropClimate{Sowing: optional(climate.SowingNow), DaysRemaining: optional(climate.GrowingDaysRemaining), OutdoorsDark: outdoorsDark, Biome: optional(v.Biome)}
		r.Facts.Calendar = colonyCalendar(climate)
	}
	if curve := v.GetPlanning().GetObserved().GetGear().GetOutdoorTemperatureByTwelfthC(); len(curve) == 12 {
		temps := make([]float64, len(curve))
		for i, t := range curve {
			temps[i] = float64(t)
		}
		r.ColdMap = domain.Known(policy.ColdMapCurve(temps))
		r.HotMap = domain.Known(policy.HotMapCurve(temps))
	}
	colonyAcquisition(v, tables, &r)
	colonyProduction(v, &r.Facts)
	var benchesErr error
	if r.ProductionBenches, benchesErr = colonyProductionBenches(v, buildings, tables.Pawns, tables.Catalog); benchesErr != nil {
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
	if err := colonyDisaster(v, &r.Facts, buildings, tables.Catalog, conditions, conditionsKnown); err != nil {
		return ColonyProjection{}, err
	}
	var comfortErr error
	if r.Facts.Comfort, comfortErr = colonyComfort(v, tables.Catalog); comfortErr != nil {
		return ColonyProjection{}, comfortErr
	}
	r.Facts.BasicComfort = r.Facts.Comfort
	r.Facts.HomeCoverage = colonyHomeCoverage(v)
	var stoneErr error
	if r.Facts.StoneStructures, stoneErr = colonyStoneStructures(v, buildings, tables.Catalog); stoneErr != nil {
		return ColonyProjection{}, stoneErr
	}
	sleeping, sleepingErr := colonySleeping(v, buildings, tables.Catalog)
	if sleepingErr != nil {
		return ColonyProjection{}, sleepingErr
	}
	r.Facts.Sleeping = sleeping
	r.Facts.AnimalUpkeep.AnimalRaces = races
	r.Facts.AnimalUpkeep.Animals = mergeHerdFoodFacts(colonyAnimals(v, tables.Pawns, races), r.FoodChannels)
	r.Facts.AnimalUpkeep.WildAnimals = colonyWildAnimals(v, tables.Pawns, races)
	if policies, known := r.Policies.Value(); known {
		eaters, eatersErr := animalFoodEaters(policies.FoodEaters, tables.Pawns, races, tables.Catalog)
		if eatersErr != nil {
			return ColonyProjection{}, eatersErr
		}
		policies.FoodEaters = eaters
		r.Policies = domain.Known(policies)
	}
	r.Facts.Waste = colonyWaste(v, tables)
	r.Facts.Pollution = colonyPollution(r.Biotech)
	var upkeepErr, medicalErr error
	if r.Facts.Upkeep, upkeepErr = colonyUpkeep(v, tables); upkeepErr != nil {
		return ColonyProjection{}, upkeepErr
	}
	if _, lit := r.Facts.Upkeep.Lighting.Value(); lit {
		if _, read := outdoorsDark.Value(); !read {
			var err error
			if outdoorsDark, err = colonyOutdoorsDark(v, tables.Catalog); err != nil {
				return ColonyProjection{}, err
			}
		}
	}
	r.Facts.OutdoorsDark = outdoorsDark
	if r.Facts.MedicalReserve, medicalErr = ColonyMedicalReserve(v, tables); medicalErr != nil {
		return ColonyProjection{}, medicalErr
	}
	// The dialog section is present exactly while a force-pausing choice
	// dialog is open; native omits it otherwise.
	r.Facts.ChoiceDialog = domain.Known(v.Dialog != nil)
	letters := make([]policy.JoinerLetterOffer, 0, len(v.JoinerLetters))
	for _, row := range v.JoinerLetters {
		letters = append(letters, policy.JoinerLetterOffer{ID: row.GetLetterId(), Token: row.GetSnapshotToken(), Pawn: domain.PawnID(row.GetPawnId()), Expires: domain.Tick(row.GetExpiresTick()), Label: row.GetAcceptLabel(), CanAccept: row.GetCanAccept(), CreepJoiner: row.GetCreepjoiner()})
	}
	r.Facts.JoinerLetters = domain.Known(letters)
	if !hasIssue(v.Issues, "cooking") && headed(buildings, v.Cooking, (*o.CookingFacts).GetBench) {
		benches := []CookingBench{}
		for _, bench := range v.Cooking {
			benches = append(benches, CookingBench{ID: bench.Bench.GetId(), Definition: buildings.Entity(bench.Bench).GetDefName(), Usable: optional(bench.Usable), Room: optionalRef(bench.Room), AutoRefuel: optional(bench.AutoRefuel)})
		}
		r.CookingBenches = domain.Known(benches)
	}
	// A food supply whose stock the things table misses stays unknown
	// until a later frame.
	if food := v.GetFoodSupply().GetObserved(); food != nil {
		supply, known, err := DecodeFoodSupply(food, tables.Things, tables.Catalog)
		if err != nil {
			return ColonyProjection{}, err
		}
		if known {
			r.FoodSupply = domain.Known(supply)
			r.Facts.FoodStorageUpkeep = policy.FoodStorageStocks(supply, float64(tables.Catalog.Derived.FullRotRateC))
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
	if loot := v.GetEventLoot().GetObserved(); loot != nil && headed(tables, loot.Items, (*o.LootItem).GetItem) {
		rows := make([]policy.LootItem, 0, len(loot.Items))
		spawnForbidden := tables.Catalog.SpawnForbiddenProducts()
		for _, row := range loot.Items {
			head := tables.Entity(row.Item)
			item := policy.LootItem{Supply: policy.StartingSupply{Thing: row.Item.GetId(), Definition: head.GetDefName(), Cell: domain.Cell{X: head.GetPosition().GetX(), Z: head.GetPosition().GetZ()}}, Forbidden: row.GetForbidden(), SafeToHaul: row.GetSafeToHaul(), SafetyKnown: row.SafeToHaul != nil, Count: row.GetCount(), SpawnForbidden: spawnForbidden[head.GetDefName()]}
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
		environment, err := colonyEnvironment(planning.Environment, v.OutdoorTemperatureC, buildings, tables.Catalog)
		if err != nil {
			return ColonyProjection{}, err
		}
		r.Environment = domain.Known(environment)
	}
	// r.Facts.Gear needs the catalog: ObserveColony fills it.
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
func colonyEnvironment(v *o.ControlledEnvironment, outdoor *float64, buildings bridge.Buildings, catalog *bridge.DefinitionCatalog) (policy.ControlledEnvironment, error) {
	e := policy.ControlledEnvironment{OutdoorTemperatureC: optional(outdoor), Daylight: optional(v.Daylight), WeatherAccuracy: domain.Unknown[float64]()}
	if v.Weather != nil {
		accuracy, err := catalog.WeatherAccuracy(v.GetWeather())
		if err != nil {
			return policy.ControlledEnvironment{}, err
		}
		e.WeatherAccuracy = domain.Known(accuracy)
	}
	for _, row := range v.Lights {
		ref := buildings.Entity(row.Building)
		power, err := defPowerW(catalog, ref.GetDefName())
		if err != nil {
			return policy.ControlledEnvironment{}, err
		}
		e.Lights = append(e.Lights, policy.GrowLight{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optionalRef(row.Room), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: power, LitNow: optional(row.LitNow), GrowthCells: cellsOf(row.GrowthCells)})
	}
	for _, row := range v.Growers {
		ref := buildings.Entity(row.Building)
		power, err := defPowerW(catalog, ref.GetDefName())
		if err != nil {
			return policy.ControlledEnvironment{}, err
		}
		fertility, sowTag := growerRows(catalog, ref.GetDefName())
		e.Growers = append(e.Growers, policy.PlantGrower{ID: ref.GetId(), Definition: ref.GetDefName(), Cell: domain.Cell{X: ref.GetPosition().GetX(), Z: ref.GetPosition().GetZ()}, Room: optionalRef(row.Room), Network: optional(row.PowerNetId), Powered: optional(row.Powered), PowerW: power, Fertility: fertility, SowTag: sowTag, Crop: optional(row.CropDefName), CanSow: optional(row.CanSow), Cells: cellsOf(row.PlantCells)})
	}
	for _, row := range v.Rooms {
		e.Rooms = append(e.Rooms, policy.GrowRoom{ID: row.GetRoom().GetId(), TemperatureC: optional(row.TemperatureC), Cells: count(row.CellCount), OpenRoof: count(row.OpenRoofCount), Lit: count(row.LitCells), Proper: optional(row.ProperRoom), Outdoors: optional(row.PsychologicallyOutdoors)})
	}
	for _, row := range v.Networks {
		e.Networks = append(e.Networks, policy.PowerHeadroom{ID: row.GetId(), GenerationW: optional(row.GenerationW), SolarW: optional(row.SolarW), WindW: optional(row.WindW), ConsumptionW: optional(row.ConsumptionW), StoredWattDays: optional(row.StoredWattDays), CapacityWattDays: optional(row.CapacityWattDays), ActiveSource: optional(row.HasActiveSource)})
	}
	return e, nil
}

// defPowerW is the base draw of def's power comp row (CompProperties_Power),
// unknown for a def without one.
func defPowerW(catalog *bridge.DefinitionCatalog, def string) (domain.Fact[float64], error) {
	watts, powered, err := catalog.PowerDraw(catalog.ThingDef(def))
	if err != nil || !powered {
		return domain.Unknown[float64](), err
	}
	return finiteFact(watts), nil
}

// growerRows is a plant grower def's own numbers: its fixed fertility (when
// it sets one) and the sow tag of its building properties, unknown for a def
// that names none.
func growerRows(catalog *bridge.DefinitionCatalog, def string) (fertility domain.Fact[float64], sowTag domain.Fact[string]) {
	row := catalog.ThingDef(def)
	fertility, sowTag = domain.Unknown[float64](), domain.Unknown[string]()
	if row != nil && row.GetFertility() >= 0 {
		fertility = finiteFact(float64(row.GetFertility()))
	}
	if tag := row.GetBuilding().GetSowTag(); tag != "" {
		sowTag = domain.Known(tag)
	}
	return fertility, sowTag
}

func count(p *uint32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}
