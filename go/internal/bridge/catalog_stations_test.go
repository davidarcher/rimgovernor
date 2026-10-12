package bridge

import (
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func recordedStations(t *testing.T) policy.StationCatalog {
	t.Helper()
	stations, err := sharedRecordedCatalog(t).StationCatalog()
	if err != nil {
		t.Fatal(err)
	}
	return stations
}

func contribution(s policy.Station, role policy.RoomRole, when policy.RoleCondition) (policy.RoleContribution, bool) {
	for _, c := range s.Contributions {
		if c.Role == role && c.When == when {
			return c, true
		}
	}
	return policy.RoleContribution{}, false
}

func need(s policy.Station, kind policy.EnvNeedKind) (policy.EnvNeed, bool) {
	for _, n := range s.Needs {
		if n.Kind == kind {
			return n, true
		}
	}
	return policy.EnvNeed{}, false
}

// A station's shape is the shape PieceShapes holds for the def.
func TestStationShapesAreThePieceShapes(t *testing.T) {
	shapes, err := sharedRecordedCatalog(t).PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	stations := recordedStations(t)
	if len(stations.Stations) < 50 {
		t.Fatalf("only %d stations", len(stations.Stations))
	}
	for def, station := range stations.Stations {
		if want := shapes.Defs[def]; !reflect.DeepEqual(station.Shape, want) {
			t.Errorf("%s shape %+v, want %+v", def, station.Shape, want)
		}
	}
}

// The role contributions are the per-building scores of the game's role
// workers, for the buildings the defs put in each role.
func TestStationRoleContributionsAreTheGamesScores(t *testing.T) {
	stations := recordedStations(t)
	for _, test := range []struct {
		def      string
		role     policy.RoomRole
		when     policy.RoleCondition
		weight   float64
		additive bool
	}{
		{"FueledStove", policy.RoomRoleKitchen, policy.CondAlways, 28, true},
		{"ElectricStove", policy.RoomRoleKitchen, policy.CondAlways, 28, true},
		{"TableStonecutter", policy.RoomRoleWorkshop, policy.CondAlways, 27, true},
		{"SimpleResearchBench", policy.RoomRoleLaboratory, policy.CondAlways, 60, true},
		{"Bed", policy.RoomRoleBedroom, policy.CondBedFree, 100000, false},
		{"Bed", policy.RoomRoleBarracks, policy.CondBedFree, 100100, true},
		{"HospitalBed", policy.RoomRoleHospital, policy.CondBedMedical, 100000, false},
		{"Bed", policy.RoomRolePrisonCell, policy.CondBedPrisoner, 170000, false},
		{"Crib", policy.RoomRoleNursery, policy.CondBedFree, 100200, true},
		{"Table2x2c", policy.RoomRoleDiningRoom, policy.CondAlways, 12, true},
		{"ChessTable", policy.RoomRoleRecRoom, policy.CondAlways, 7, true},
		{"Shelf", policy.RoomRoleStoreroom, policy.CondAlways, 1, true},
		{"AnimalBed", policy.RoomRoleBarn, policy.CondAlways, 7.6, true},
		{"Throne", policy.RoomRoleThroneRoom, policy.CondAlways, 10000, false},
		{"Sarcophagus", policy.RoomRoleTomb, policy.CondAlways, 50, true},
		{"SchoolDesk", policy.RoomRoleClassroom, policy.CondAlways, 8, true},
		{"ToyBox", policy.RoomRolePlayroom, policy.CondAlways, 8, true},
		{"DeathrestCasket", policy.RoomRoleDeathrestChamber, policy.CondAlways, 100, true},
		{"Hemopump", policy.RoomRoleDeathrestChamber, policy.CondBindable, 50, true},
		{"PsychicRitualSpot", policy.RoomRoleCeremonialChamber, policy.CondAlways, 200, true},
		{"Altar_Small", policy.RoomRoleWorshipRoom, policy.CondIdeoAltar, 75, true},
	} {
		station, ok := stations.Station(test.def)
		if !ok {
			t.Errorf("%s is not a station", test.def)
			continue
		}
		got, ok := contribution(station, test.role, test.when)
		if !ok || got.Weight != test.weight || got.Additive != test.additive {
			t.Errorf("%s %s/%q: %+v (found %v), want weight %v additive %v", test.def, test.role, test.when, got, ok, test.weight, test.additive)
		}
	}
	if c, _ := contribution(stations.Stations["Altar_Small"], policy.RoomRoleWorshipRoom, policy.CondIdeoAltar); c.Floor != 2000 {
		t.Errorf("worship floor %v, want 2000", c.Floor)
	}
	// The membership comes from the defs: a campfire is no production
	// building, a plain bed no medical one, a small bench no stove.
	if _, ok := contribution(stations.Stations["TableStonecutter"], policy.RoomRoleKitchen, policy.CondAlways); ok {
		t.Error("a stonecutter counts for the kitchen")
	}
	if _, ok := stations.Station("Campfire"); !ok {
		t.Error("the campfire hosts bills and is a station")
	} else if _, ok := contribution(stations.Stations["Campfire"], policy.RoomRoleKitchen, policy.CondAlways); ok {
		t.Error("the campfire is no production building")
	}
	if _, ok := contribution(stations.Stations["AnimalBed"], policy.RoomRoleBedroom, policy.CondBedFree); ok {
		t.Error("an animal bed counts for bedrooms")
	}
	if _, ok := contribution(stations.Stations["Bedroll"], policy.RoomRoleNursery, policy.CondBedFree); ok {
		t.Error("an adult bedroll counts for the nursery")
	}
	if got := stations.Contributors(policy.RoomRoleLaboratory); !reflect.DeepEqual(got, sortedCopy(got)) || len(got) < 5 {
		t.Errorf("laboratory contributors %v", got)
	}
	if hosts := stations.Hosting(policy.CapCook); len(hosts) == 0 || !contains(hosts, "FueledStove") {
		t.Errorf("cook hosts %v", hosts)
	}
}

func sortedCopy(in []string) []string {
	out := append([]string(nil), in...)
	for i := range out {
		for j := i + 1; j < len(out); j++ {
			if out[j] < out[i] {
				out[i], out[j] = out[j], out[i]
			}
		}
	}
	return out
}

func contains(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}

// Environment needs come from the stat parts, place workers and storage
// filters of the defs.
func TestStationEnvironmentNeedsAreReadFromTheGame(t *testing.T) {
	stations := recordedStations(t)
	bed, ok := need(stations.Stations["HospitalBed"], policy.NeedRoomStat)
	if !ok || bed.Input != "Cleanliness" || bed.Via != "SurgerySuccessChanceFactor" || bed.Base != float64(float32(1.1)) {
		t.Errorf("hospital bed need %+v (found %v)", bed, ok)
	}
	stove := stations.Stations["FueledStove"]
	if n, ok := need(stove, policy.NeedRole); !ok || n.Role != policy.RoomRoleKitchen || float32(n.Factor) != 0.8 {
		t.Errorf("stove role need %+v", n)
	}
	if n, ok := need(stove, policy.NeedTemperature); !ok || n.Min != 9 || n.Max != 35 || float32(n.Factor) != 0.7 {
		t.Errorf("stove temperature need %+v", n)
	}
	if n, ok := need(stove, policy.NeedIndoors); !ok || n.Factor <= 0 {
		t.Errorf("stove indoors need %+v", n)
	}
	if n, ok := need(stations.Stations["SimpleResearchBench"], policy.NeedRoomStat); !ok || n.Input != "Cleanliness" {
		t.Errorf("research bench need %+v", n)
	}
	if _, ok := need(stations.Stations["Shelf"], policy.NeedCold); !ok {
		t.Error("a shelf takes perishables and wants cold")
	}
	if _, ok := need(stations.Stations["Burnbong"], policy.NeedIndoors); !ok {
		t.Error("a burnbong must be indoors")
	}
	if _, ok := need(stations.Stations["OrbitalScanner"], policy.NeedOpenSky); !ok {
		t.Error("an orbital scanner needs open sky")
	}
	if len(stations.Stations["Throne"].Needs) != 0 {
		t.Errorf("throne needs %v", stations.Stations["Throne"].Needs)
	}
}

func TestStationCapacityAccessAndContent(t *testing.T) {
	stations := recordedStations(t)
	if c := stations.Stations["DoubleBed"].Capacity; c.Kind != policy.CapacitySleepers || c.Slots != 2 {
		t.Errorf("double bed %+v", c)
	}
	if c := stations.Stations["Shelf"].Capacity; c.Kind != policy.CapacityItems || c.Slots != 6 {
		t.Errorf("shelf %+v", c)
	}
	c := stations.Stations["FueledStove"].Capacity
	if c.Kind != policy.CapacityWorkers || c.Slots != 1 {
		t.Fatalf("stove %+v", c)
	}
	var simple float64
	for _, r := range c.Recipes {
		if r.Recipe == "CookMealSimple" {
			simple = r.Work
		}
	}
	if simple != 300 {
		t.Errorf("simple meal work %v, want the recipe's 300", simple)
	}
	for def, want := range map[string]policy.StationAccess{"WallLamp": policy.AccessWall, "Vent": policy.AccessWall, "Bed": policy.AccessFloor} {
		if s, ok := stations.Station(def); !ok || s.Access != want {
			t.Errorf("%s access %q (found %v), want %q", def, s.Access, ok, want)
		}
	}
	if crib := stations.Stations["Crib"].Content; !strings.Contains(crib, "biotech") {
		t.Errorf("crib content %q", crib)
	}
	if bed := stations.Stations["Bed"].Content; bed == "" || strings.Contains(bed, "biotech") {
		t.Errorf("bed content %q", bed)
	}
}

func TestStationCatalogIsStable(t *testing.T) {
	a, b := recordedStations(t), recordedStations(t)
	if !reflect.DeepEqual(a, b) {
		t.Error("two reads differ")
	}
}

func TestStationCatalogNeedsACatalog(t *testing.T) {
	var none *DefinitionCatalog
	if _, err := none.StationCatalog(); err == nil {
		t.Error("no catalog, no stations")
	}
}

// Cooling stations are the buildings whose temperature control draws heat out,
// with the power they draw.
func TestStationCatalogHasCoolers(t *testing.T) {
	stations := recordedStations(t)
	coolers := stations.Coolers()
	if len(coolers) == 0 {
		t.Fatal("no cooling station in the catalog")
	}
	for _, def := range coolers {
		c := stations.Stations[def].Cooling
		if c.HeatPerSecond <= 0 || c.PowerDraw <= 0 {
			t.Errorf("%s cooling %+v", def, c)
		}
	}
	if !slices.Contains(coolers, "Cooler") {
		t.Errorf("coolers %v lack the Cooler", coolers)
	}
	if stations.Stations["Heater"].Cooling.Cools() {
		t.Error("a heater cools")
	}
}

// The climate model is the game's numbers: the vent's TickRare interval and
// 14f rate, the Door def's equalize fields (the TemperatureTuning defaults),
// the Heater's and Cooler's temperature control and power comps.
func TestStationCatalogClimateModelIsTheGamesNumbers(t *testing.T) {
	m := recordedStations(t).Climate
	if !m.Valid() {
		t.Fatalf("climate model %+v", m)
	}
	if m.Vent.IntervalTicks != 250 || m.Vent.Rate != 14 {
		t.Errorf("vent %+v, want TickRare 250 and rate 14", m.Vent)
	}
	if m.DoorOpen.IntervalTicks != 34 || m.DoorClosed.IntervalTicks != 375 || m.DoorOpen.Rate != 1 || m.DoorClosed.Rate != 1 {
		t.Errorf("door open %+v closed %+v", m.DoorOpen, m.DoorClosed)
	}
	if m.SwingC != 7 || m.StepTicks != 250 || m.TicksPerSecond != 60 {
		t.Errorf("swing %v step %d ticks per second %v", m.SwingC, m.StepTicks, m.TicksPerSecond)
	}
	if m.Heater.HeatPerSecond != 21 || m.Heater.PowerW <= 0 {
		t.Errorf("heater %+v", m.Heater)
	}
	if m.Build.Vent <= 0 || m.Build.Door <= 0 || m.Build.Wall <= 0 {
		t.Errorf("build values %+v", m.Build)
	}
	if !m.HasLiving() || m.Living.MinC != 16 || m.Living.MaxC != 26 || m.HeldC != 21 {
		t.Errorf("living band %+v held at %v, want the Human's 16..26 and the heater's 21", m.Living, m.HeldC)
	}
	t.Logf("%+v", m)
}

// A station that pushes heat carries the def's heat per second and limits
// (CompProperties_HeatPusher): the electric stove 3 a second at any
// temperature, the campfire 21 a second below 28 C.
func TestStationCatalogHeatIsTheHeatPushersNumbers(t *testing.T) {
	stations := recordedStations(t)
	if h := stations.Stations["ElectricStove"].Heat; h.HeatPerSecond != 3 || h.MaxC < 1000 {
		t.Errorf("electric stove heat %+v", h)
	}
	if h := stations.Stations["Campfire"].Heat; h.HeatPerSecond != 21 || h.MaxC != 28 {
		t.Errorf("campfire heat %+v", h)
	}
	if h := stations.Stations["Heater"].Heat; h.Pushes() {
		t.Errorf("a heater pushes through its temperature control, not a heat pusher: %+v", h)
	}
}
