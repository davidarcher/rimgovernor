package policy

import (
	"errors"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// deriveFacts are the station facts of a first plan: a survey, the colonists
// and the roof distance. The census, the orders and the stock are unread, the
// catalog is the test one and nothing stands.
func deriveFacts(colonists int, cold, hot bool, curve []float64) StationFacts {
	s := plainScoreSurvey()
	s.Cold, s.Hot = cold, hot
	cat := clusterCatalog()
	cat.Climate = testClimate()
	return StationFacts{
		Catalog: cat, Dining: DiningFurniture{Table: InteriorPieceDef{Def: "Table", Size: domain.Cell{X: 1, Z: 2}}},
		Colonists: domain.Known(int64(colonists)), Items: ItemFacts{Market: map[Resource]float64{siteSteelUnit: 1.9}},
		Plan: LayoutPlan{Zones: Zone(s)}, Built: map[int32]bool{}, Survey: s, RoofDistance: testRoof,
		Derive: true, OutdoorCurve: domain.Known(curve), FullRotRateC: 10,
	}
}

func tiledRoom(t *testing.T, a StationAdvice, role PlannedRole) (PlannedRoom, int) {
	t.Helper()
	if a.Tiled == nil {
		t.Fatal("no tiled plan")
	}
	for gi, id := range a.Tiled.Rooms {
		for _, r := range a.Tiled.Plan.Rooms {
			if r.ID == id && r.Role == role {
				return r, gi
			}
		}
	}
	t.Fatalf("the tiled plan holds no %s: %+v", role, a.Tiled.Plan.Rooms)
	return PlannedRoom{}, 0
}

func TestSeedRoomNodesAskTheOldSitingsFloor(t *testing.T) {
	nodes := SeedRoomNodes(SeedRooms{Shelter: true, Colonists: 4, Cold: true, ThroneArea: 30})
	if len(nodes) != 2 || nodes[0].Role != PlannedShelter || nodes[1].Role != PlannedThrone {
		t.Fatalf("nodes %+v", nodes)
	}
	if want := ShelterInteriorArea(4, ShelterCampfires(true), ShelterCoolers(false)); nodes[0].Area != want {
		t.Errorf("shelter area %d, want %d", nodes[0].Area, want)
	}
	if nodes[1].Area != 30 || nodes[0].Reason != ReasonShelterRoom || nodes[1].Reason != ReasonThroneRoom {
		t.Errorf("nodes %+v", nodes)
	}
	if got := SeedRoomNodes(SeedRooms{}); len(got) != 0 {
		t.Errorf("an empty ask holds rooms: %+v", got)
	}
}

// A first plan has no census, orders or stock: the graph keeps the seed rooms
// and the outskirts, names the fact it lacked, and tiles a shelter.
func TestDeriveTimeFactsTileTheShelterWithoutAProjection(t *testing.T) {
	for _, tc := range []struct {
		name  string
		cold  bool
		curve []float64
	}{{"cold", true, seaIce}, {"temperate", false, []float64{8, 10, 14, 18, 20, 22, 22, 21, 18, 14, 10, 8}}} {
		t.Run(tc.name, func(t *testing.T) {
			a, err := TileStations(deriveFacts(4, tc.cold, false, tc.curve))
			if err != nil {
				t.Fatal(err)
			}
			if a.SeedOnly == "" || len(a.Graph.Nodes) != 0 {
				t.Fatalf("seed only %q, nodes %d", a.SeedOnly, len(a.Graph.Nodes))
			}
			shelter, gi := tiledRoom(t, a, PlannedShelter)
			if want := ShelterInteriorArea(4, ShelterCampfires(tc.cold), 0); shelter.Region.Area() < want {
				t.Errorf("shelter floor %d, want at least %d", shelter.Region.Area(), want)
			}
			airlock := 0
			for _, d := range a.Tiled.Decisions {
				if d.Group == gi && (d.Reason == ReasonTileAirlock || d.Reason == ReasonTileAirlockNotPaid || d.Reason == ReasonTileAirlockNoGround) {
					airlock++
				}
			}
			if cold := tc.cold; cold && airlock != 1 {
				t.Errorf("the cold shelter has %d airlock decisions: %+v", airlock, a.Tiled.Decisions)
			}
		})
	}
}

func TestDeriveTimeThroneRoomIsTheRequestedArea(t *testing.T) {
	f := deriveFacts(4, false, false, []float64{8, 10, 14, 18, 20, 22, 22, 21, 18, 14, 10, 8})
	f.ThroneArea = domain.Known(30)
	a, err := TileStations(f)
	if err != nil {
		t.Fatal(err)
	}
	throne, _ := tiledRoom(t, a, PlannedThrone)
	if got := throne.Region.Area(); got < 30 {
		t.Errorf("throne floor %d, want at least 30", got)
	}
	if _, ok := a.Tiled.Plan.ThroneRoomFor(30); !ok {
		t.Errorf("the plan does not hold a throne room for 30 cells")
	}
}

// Unknown stays unknown: no throne area read asks for no throne room, an
// unread colony size cannot size the shelter, and a live advice (not Derive)
// still fails on a fact it lacks.
func TestDeriveTimeUnknownFactsAskForNothing(t *testing.T) {
	f := deriveFacts(4, false, false, nil)
	a, err := TileStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(a.Tiled.Plan.Rooms, func(r PlannedRoom) bool { return r.Role == PlannedThrone }) {
		t.Errorf("a throne room without a read area: %+v", a.Tiled.Plan.Rooms)
	}
	f.Colonists = domain.Unknown[int64]()
	if _, err := AdviseStations(f); !errors.Is(err, ErrStationFacts) {
		t.Errorf("an unread colony size: %v", err)
	}
	f = deriveFacts(4, false, false, nil)
	f.RoofDistance = 0
	if _, err := AdviseStations(f); !errors.Is(err, ErrStationFacts) {
		t.Errorf("an unread roof distance: %v", err)
	}
	f = deriveFacts(4, false, false, nil)
	f.Derive = false
	if _, err := AdviseStations(f); !errors.Is(err, ErrStationFacts) {
		t.Errorf("a live advice without the census: %v", err)
	}
}

// The seed rooms are owed to a fresh plan only: a plan that holds a shelter or
// has anything standing asks for none, and a throne room the plan holds is not
// asked again.
func TestSeedRoomsAreOwedToAFreshPlanOnly(t *testing.T) {
	f := deriveFacts(4, false, false, nil)
	f.ThroneArea = domain.Known(30)
	if s, err := f.seedRooms(); err != nil || !s.Shelter || s.Colonists != 4 || s.ThroneArea != 30 {
		t.Fatalf("fresh plan: %+v %v", s, err)
	}
	held := f
	held.Plan.Rooms = []PlannedRoom{{ID: 1, Role: PlannedShelter, Region: region(4, 4)}, {ID: 2, Role: PlannedThrone, Region: region(6, 6)}}
	if s, _ := held.seedRooms(); s.Shelter || s.ThroneArea != 0 {
		t.Errorf("held rooms asked again: %+v", s)
	}
	standing := f
	standing.Built = map[int32]bool{7: true}
	if s, _ := standing.seedRooms(); s.Shelter || s.ThroneArea != 30 {
		t.Errorf("a plan with a room standing owes no shelter: %+v", s)
	}
}
