package policy

import (
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func openSurvey(size int32) MapSurvey {
	return zoningSurvey(size, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
}

// A fresh plan is the tiled plan with the perimeter, the yard and the barn
// laid over it, its doors joined by Links, and no core candidates saved.
func TestDeriveLayoutPlanLaysTheTiledPlan(t *testing.T) {
	s := openSurvey(200)
	fact, advice, err := DeriveLayoutPlan(s, colonyStationFacts(3, s), nil, 30)
	plan, ok := fact.Value()
	if err != nil || !ok || !plan.Valid() {
		t.Fatalf("plan %v %v %v", ok, err, plan.Valid())
	}
	if advice.Tiled == nil || advice.SeedOnly != "" {
		t.Fatalf("advice %+v seed only %q", advice.Tiled == nil, advice.SeedOnly)
	}
	for _, z := range plan.Zones {
		if z.Kind == ZoneCore {
			t.Fatal("core candidates saved")
		}
	}
	if len(plan.Links) == 0 || len(plan.Entrances) == 0 {
		t.Errorf("links %d, entrances %d", len(plan.Links), len(plan.Entrances))
	}
	if len(plan.roomsOf(PlannedShelter)) != 1 || (bedroomCount(plan) < 3) {
		t.Errorf("shelter %d, outgrown %v: %s", len(plan.roomsOf(PlannedShelter)), (bedroomCount(plan) < 3), plan.Summary())
	}
	if _, ring := plan.Ring(); !ring {
		t.Error("no perimeter ring")
	}
	sum := plan.Summary()
	for _, want := range []string{"barn:", "yard:", "perimeter_wall:", "routes=ok"} {
		if !strings.Contains(sum, want) {
			t.Errorf("summary lacks %s: %s", want, sum)
		}
	}
	if plan.TombRooms() != 1 {
		t.Errorf("the outskirts hold %d tombs", plan.TombRooms())
	}
	again, _, _ := DeriveLayoutPlan(s, colonyStationFacts(3, s), nil, 30)
	if got, _ := again.Value(); !reflect.DeepEqual(got, plan) {
		t.Error("the derive is not deterministic")
	}
}

// A geyser's enclosure is reserved inside core ground and no room covers it.
func TestDeriveLayoutPlanKeepsRoomsOffGeysers(t *testing.T) {
	s := openSurvey(200)
	base, _, _ := DeriveLayoutPlan(s, colonyStationFacts(5, s), nil, 0)
	plan, _ := base.Value()
	for i, r := range plan.Rooms {
		if i%4 != 0 { // a geyser under every fourth room covers each role
			continue
		}
		at := domain.Cell{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}
		g := []PowerGeyser{{ID: "g", Cell: at, Cells: []domain.Cell{at, {X: at.X + 1, Z: at.Z}, {X: at.X, Z: at.Z + 1}, {X: at.X + 1, Z: at.Z + 1}}}}
		vents := geothermalCells(geyserFootprints(g))
		fact, _, err := DeriveLayoutPlan(s, colonyStationFacts(5, s), g, 0)
		next, ok := fact.Value()
		if err != nil || !ok {
			t.Fatalf("no plan with a geyser under %s: %v", r.Role, err)
		}
		for _, room := range next.Rooms {
			if rectHits(roomWalls(room), vents) {
				t.Fatalf("%s covers the geyser at %v", room.Role, at)
			}
		}
		if sites := PlannedPowerSites(next, GeothermalDefinition); len(sites) != 1 || sites[0].Cell != at {
			t.Fatalf("geothermal sites %+v for the geyser at %v", sites, at)
		}
	}
}

// The shelter gap is gone (#2987): the shelter is a room like the rest, so on
// tight ground the derive still places it: it does at 36x36 (the old siting
// needed 48).
func TestDeriveLayoutPlanShelterOnTightGround(t *testing.T) {
	placed := 0
	for size := int32(30); size <= 64; size += 2 {
		s := openSurvey(size)
		fact, _, err := DeriveLayoutPlan(s, colonyStationFacts(3, s), nil, 0)
		plan, _ := fact.Value()
		if err != nil {
			t.Fatalf("size %d: %v", size, err)
		}
		placed += len(plan.roomsOf(PlannedShelter))
	}
	if placed == 0 {
		t.Fatal("no shelter placed on any size")
	}
	// The derive places a shelter on 40x40 ground.
	s := openSurvey(40)
	fact, _, _ := DeriveLayoutPlan(s, colonyStationFacts(3, s), nil, 0)
	if plan, _ := fact.Value(); len(plan.roomsOf(PlannedShelter)) != 1 {
		t.Errorf("no shelter on 40x40 ground: %s", plan.Summary())
	}
}

// A first plan whose orders and stock are unread derives the seed rooms (the
// shelter and a throne room owed) and the outskirts, and says which fact it
// lacked; an unread colony size is no plan.
func TestDeriveLayoutPlanFromSeedRooms(t *testing.T) {
	s := openSurvey(200)
	f := colonyStationFacts(4, s)
	f.Orders, f.Stock = domain.Unknown[[]OrderSpec](), domain.Unknown[map[Resource]int64]()
	f.ThroneArea = domain.Known(30)
	fact, advice, err := DeriveLayoutPlan(s, f, nil, 0)
	plan, ok := fact.Value()
	if err != nil || !ok || advice.SeedOnly == "" {
		t.Fatalf("seed-only plan: %v %v %q", ok, err, advice.SeedOnly)
	}
	if len(plan.roomsOf(PlannedShelter)) != 1 {
		t.Errorf("no shelter: %s", plan.Summary())
	}
	if _, held := plan.ThroneRoomFor(30); !held {
		t.Errorf("no throne room: %s", plan.Summary())
	}
	if _, ring := plan.Ring(); !ring {
		t.Error("no perimeter ring")
	}
	f.Colonists = domain.Unknown[int64]()
	if fact, _, err := DeriveLayoutPlan(s, f, nil, 0); !errors.Is(err, ErrStationFacts) {
		t.Errorf("an unread colony size: %v", err)
	} else if _, ok := fact.Value(); ok {
		t.Error("a plan without a colony size")
	}
}
