package policy

import (
	"slices"
	"sort"
)

// StationCatalog is every buildable station the game offers, read from its
// definitions and room-role code (bridge builds it). A station is a building
// that adds to a native room role, has a capacity, or needs something of the
// room it stands in. The planner reasons about stations; rooms emerge from
// clustering them, and a capability is only a derived grouping of station
// classes (Hosting).
type StationCatalog struct {
	Stations map[string]Station
	// Filth prices the filth a butchering spawns (FilthModel); the zero value
	// prices nothing.
	Filth FilthModel
	// Climate is the heat the vent, door, heater and cooler move, from the
	// defs and the game's constants (#2940); the zero value is unknown.
	Climate ClimateModel
}

// Station is one buildable definition as the planner sees it.
type Station struct {
	Def string
	// Shape is the footprint: size, interaction cell and slot family, the
	// same value PieceShapes holds for the def.
	Shape InteriorPieceDef
	// Access is where the footprint may stand.
	Access StationAccess
	// Contributions are the native room-role scores the building adds, as
	// RoomRoleWorker.GetScoreDeltaIfBuildingPlaced does.
	Contributions []RoleContribution
	// Capacity is what one station serves.
	Capacity StationCapacity
	// EmptyCountsForBarracks is a humanlike bed's bed_emptyCountsForBarracks:
	// an unassigned bed counts toward the barracks test.
	EmptyCountsForBarracks bool
	// Needs are what the station wants of its surroundings.
	Needs []EnvNeed
	// Cooling is set when the station cools its room (a temperature control
	// that draws heat out); the zero value cools nothing.
	Cooling StationCooling
	// BaseW is the def's power comp wattage in the sign the power policy reads
	// (PowerBuilding.BaseW): negative for a consumer, positive for a producer,
	// zero for none or a battery. The conduit router wires planned rooms
	// holding a station with BaseW < 0 (DrawsPower).
	BaseW float64
	// Content is the package id of the content that defines the station
	// ("ludeon.rimworld" for the base game, an expansion's id otherwise); the
	// station is plannable only while that content is active.
	Content string
	// Minifiable is ThingDef.Minifiable (minifiedDef set): the built building
	// can be uninstalled and reinstalled elsewhere (BuiltPiece.Movable).
	Minifiable bool
}

// DrawsPower reports a station that consumes grid power.
func (s Station) DrawsPower() bool { return s.BaseW < 0 }

// StationAccess says whether a station stands on open floor or in a wall.
type StationAccess string

const (
	// AccessFloor is an ordinary footprint on a floor cell.
	AccessFloor StationAccess = "floor"
	// AccessWall is a building that replaces or attaches to a wall (a door,
	// a vent, a wall lamp): canPlaceOverWall or the attached-to-wall place
	// worker.
	AccessWall StationAccess = "wall"
)

// RoleCondition names the instance state a definition cannot decide, which
// the game's role worker tests on the building itself.
type RoleCondition string

const (
	// CondAlways: the definition alone decides.
	CondAlways RoleCondition = ""
	// CondBedFree: a bed neither medical nor set for prisoners.
	CondBedFree RoleCondition = "bed_free"
	// CondBedMedical: a medical bed not set for prisoners.
	CondBedMedical RoleCondition = "bed_medical"
	// CondBedPrisoner: a prisoner bed that is not medical.
	CondBedPrisoner RoleCondition = "bed_prisoner"
	// CondBedPrisonerMedical: a medical prisoner bed.
	CondBedPrisonerMedical RoleCondition = "bed_prisoner_medical"
	// CondIdeoAltar: an altar whose precept carries an ideo structure meme.
	CondIdeoAltar RoleCondition = "ideo_altar"
	// CondBindable: a deathrest-bindable building that is not the casket.
	CondBindable RoleCondition = "bindable"
)

// RoleScore is the weight the game's role worker gives one building under a
// condition. Additive scores add per building (kitchen 28 per stove); a Flat
// score is a gate the role worker applies once (a bedroom is 100000 however
// many beds). Floor is the least score once any building counts (the worship
// room's 2000).
type RoleScore struct {
	Role     RoomRole
	When     RoleCondition
	Weight   float64
	Additive bool
	Floor    float64
}

// RoleContribution is a RoleScore a given station earns.
type RoleContribution = RoleScore

// CapacityKind is the unit of a station's capacity.
type CapacityKind string

const (
	CapacityNone CapacityKind = ""
	// CapacitySleepers: Slots sleepers (BedUtility.GetSleepingSlotsCount).
	CapacitySleepers CapacityKind = "sleepers"
	// CapacityItems: Slots stacks held (cells times maxItemsInCell).
	CapacityItems CapacityKind = "items"
	// CapacityWorkers: one worker at a time; Recipes give the work per unit.
	CapacityWorkers CapacityKind = "workers"
)

// StationCapacity is a station's throughput. A work table serves one worker
// (Slots 1); the work one unit of each recipe takes is in Recipes, so
// throughput is 60000 ticks a day over the amount at the worker's speed.
type StationCapacity struct {
	Kind    CapacityKind
	Slots   int
	Recipes []RecipeWorkAmount
}

// RecipeWorkAmount is the work one unit of a recipe takes at the station.
type RecipeWorkAmount struct {
	Recipe string
	Work   float64
}

// EnvNeedKind says what a station wants of the room around it.
type EnvNeedKind string

const (
	// NeedRoomStat: the station's effect scales with a room stat. Stat is the
	// RoomStatDef a StatPart_RoomStat reads, Input the RoomStatDef at the end
	// of its inputStat chain (Cleanliness for surgery success), Via the StatDef
	// carrying the part and Base that stat's value on the station.
	NeedRoomStat EnvNeedKind = "room_stat"
	// NeedTemperature: the work rate is multiplied by Factor outside
	// [Min, Max] degrees (StatPart_WorkTableTemperature).
	NeedTemperature EnvNeedKind = "temperature"
	// NeedIndoors: Factor applies when the room is outdoors
	// (StatPart_WorkTableOutdoors, PlaceWorker_MustBeIndoors: Factor 0 there).
	NeedIndoors EnvNeedKind = "indoors"
	// NeedOpenSky: the building cannot stand under a roof
	// (PlaceWorker_NotUnderRoof).
	NeedOpenSky EnvNeedKind = "open_sky"
	// NeedRole: the work rate is multiplied by Factor when the room's native
	// role is not Role (StatPart_WorkTableRoomRole).
	NeedRole EnvNeedKind = "role"
	// NeedCold: the station stores things that rot, so it wants a room kept
	// cold. The rot curve is game code (GenTemperature.RotRateAtTemperature),
	// so no numbers are stated.
	NeedCold EnvNeedKind = "cold"
)

// EnvNeed is one environment need. Which fields are set depends on Kind.
type EnvNeed struct {
	Kind     EnvNeedKind
	Stat     string
	Input    string
	Via      string
	Role     RoomRole
	Base     float64
	Min, Max float64
	Factor   float64
}

// Station returns the station for a definition.
func (c StationCatalog) Station(def string) (Station, bool) {
	s, ok := c.Stations[def]
	return s, ok
}

// Defs lists the station definitions, sorted.
func (c StationCatalog) Defs() []string {
	out := make([]string, 0, len(c.Stations))
	for def := range c.Stations {
		out = append(out, def)
	}
	sort.Strings(out)
	return out
}

// Contributors lists, sorted, the stations that add to a role.
func (c StationCatalog) Contributors(role RoomRole) []string {
	var out []string
	for _, def := range c.Defs() {
		for _, contribution := range c.Stations[def].Contributions {
			if contribution.Role == role {
				out = append(out, def)
				break
			}
		}
	}
	return out
}

// Hosting lists, sorted, the stations of the classes that provide a
// capability: the capability is the grouping, the station family its
// census role (capabilityHomes).
func (c StationCatalog) Hosting(capability Capability) []string {
	home, ok := capabilityHomes[capability]
	if !ok {
		return nil
	}
	var out []string
	for _, def := range c.Defs() {
		if c.Stations[def].Shape.Family == home {
			out = append(out, def)
		}
	}
	return out
}

// roomRoleScores are the weights of the game's RoomRoleWorker.GetScore (and
// GetScoreDeltaIfBuildingPlaced) per building. They are literals in the
// game's code, not in any def, so the catalog cannot read them; they were
// read by decompiling 1.6 and are a DefTables entry keyed by RoomRoleDef so
// a renamed role fails the dangling-reference gate. Which buildings earn a
// score is read from the defs (bridge).
var roomRoleScores = []RoleScore{
	{Role: RoomRoleKitchen, Weight: 28, Additive: true},
	{Role: RoomRoleWorkshop, Weight: 27, Additive: true},
	{Role: RoomRoleLaboratory, Weight: 60, Additive: true},
	{Role: RoomRoleDiningRoom, Weight: 12, Additive: true},
	{Role: RoomRoleRecRoom, Weight: 7, Additive: true},
	{Role: RoomRoleStoreroom, Weight: 1, Additive: true},
	{Role: RoomRoleBarn, Weight: 7.6, Additive: true},
	{Role: RoomRoleTomb, Weight: 50, Additive: true},
	{Role: RoomRoleBedroom, When: CondBedFree, Weight: 100000},
	{Role: RoomRoleBarracks, When: CondBedFree, Weight: 100100, Additive: true},
	{Role: RoomRoleNursery, When: CondBedFree, Weight: 100200, Additive: true},
	{Role: RoomRoleHospital, When: CondBedMedical, Weight: 100000},
	{Role: RoomRolePrisonCell, When: CondBedPrisoner, Weight: 170000},
	{Role: RoomRolePrisonCell, When: CondBedPrisonerMedical, Weight: 100000},
	{Role: RoomRolePrisonBarracks, When: CondBedPrisoner, Weight: 100100, Additive: true},
	{Role: RoomRolePrisonBarracks, When: CondBedPrisonerMedical, Weight: 50001, Additive: true},
	{Role: RoomRoleThroneRoom, Weight: 10000},
	{Role: RoomRoleWorshipRoom, When: CondIdeoAltar, Weight: 75, Additive: true, Floor: 2000},
	{Role: RoomRoleClassroom, Weight: 8, Additive: true},
	{Role: RoomRolePlayroom, Weight: 8, Additive: true},
	{Role: RoomRoleDeathrestChamber, Weight: 100, Additive: true},
	{Role: RoomRoleDeathrestChamber, When: CondBindable, Weight: 50, Additive: true},
	{Role: RoomRoleCeremonialChamber, Weight: 200, Additive: true},
}

// RoomRoleScore is the game's weight for a role under a condition.
func RoomRoleScore(role RoomRole, when RoleCondition) (RoleScore, bool) {
	for _, s := range roomRoleScores {
		if s.Role == role && s.When == when {
			return s, true
		}
	}
	return RoleScore{}, false
}

func roomRoleScoreRoles() []string {
	var out []string
	for _, s := range roomRoleScores {
		if !slices.Contains(out, string(s.Role)) {
			out = append(out, string(s.Role))
		}
	}
	sort.Strings(out)
	return out
}

// RoomRoleNamedDefs are the buildings the game's role workers name through
// ThingDefOf rather than by a trait of the def: the deathrest casket (the
// bindable test counts every other bindable building half) and the psychic
// ritual spot. The game offers no def signal for either.
var RoomRoleNamedDefs = map[string]RoomRole{
	"DeathrestCasket":   RoomRoleDeathrestChamber,
	"PsychicRitualSpot": RoomRoleCeremonialChamber,
}

// StationCooling is what a cooling station does, from its
// CompProperties_TempControl and power comp: the heat it moves per second
// (the def's negative energyPerSecond, as a positive number) and the power it
// draws.
type StationCooling struct {
	HeatPerSecond float64
	PowerDraw     float64
}

// Cools reports whether the station cools its room.
func (c StationCooling) Cools() bool { return c.HeatPerSecond > 0 }

// Coolers lists, sorted, the stations that cool their room.
func (c StationCatalog) Coolers() []string {
	var out []string
	for _, def := range c.Defs() {
		if c.Stations[def].Cooling.Cools() {
			out = append(out, def)
		}
	}
	return out
}
