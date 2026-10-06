package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func morgueFixture() (LayoutPlan, PlannedRoom) {
	room := PlannedRoom{Role: PlannedMorgue, Interior: Rectangle{X: 10, Z: 20, Width: 5, Height: 4}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	return LayoutPlan{Rooms: []PlannedRoom{room}}, room
}

func TestMorgueReconciledForAnyHumanCorpse(t *testing.T) {
	plan, room := morgueFixture()
	for _, waste := range [][]WasteItem{
		{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger, RotStage: domain.RotFresh}},
		{{ID: "Corpse_2", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger, RotStage: domain.RotRotting}},
		{{ID: "Corpse_3", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist, RotStage: domain.RotFresh}},
	} {
		if got, owed := MorgueRoomOwed(plan, GroundCensus{}, waste); !owed || !got.Same(room) {
			t.Fatalf("%+v: %+v %v", waste, got, owed)
		}
	}
	buried := []WasteItem{{ID: "Corpse_4", Kind: "corpse", State: WasteBuried, CorpseOf: domain.CorpseColonist}}
	animal := []WasteItem{{ID: "Corpse_5", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseAnimal, RotStage: domain.RotFresh}}
	for _, waste := range [][]WasteItem{buried, animal, nil} {
		if _, owed := MorgueRoomOwed(plan, GroundCensus{}, waste); owed {
			t.Fatalf("owed for %+v", waste)
		}
	}
	human := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger}}
	if _, owed := MorgueRoomOwed(plan, ringWalls(plan, room), human); owed {
		t.Fatal("a standing morgue owes no shell")
	}
}

func TestMorgueIsCooledOnceStanding(t *testing.T) {
	plan, room := morgueFixture()
	rooms := tombStanding(PlannedRoom{Interior: room.Interior})
	rooms.Rooms[0].Temperature = domain.Known(18.0)
	if got, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); !known || !reflect.DeepEqual(got, []string{"r1"}) {
		t.Fatalf("a standing warm morgue owes cooling: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Known(-6.0)
	if got, _ := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); len(got) != 0 {
		t.Fatalf("a frozen morgue is done: %v", got)
	}
}

func TestMorgueSiteHoldsFreshStrangersAheadOfTheDump(t *testing.T) {
	plan, room := morgueFixture()
	rooms := tombStanding(PlannedRoom{Interior: room.Interior})
	sites := StorageRequest{Layout: &plan, Rooms: &rooms}.morgueSites()
	if len(sites) != 1 || sites[0].Role != domain.MorgueRolePrefix+"r1" || sites[0].Filter != domain.MorgueCorpsesFilter() {
		t.Fatalf("sites %+v", sites)
	}
	if sites[0].Priority != domain.CriticalPriority {
		t.Fatalf("priority %v", sites[0].Priority)
	}
	if got := (StorageRequest{Layout: &LayoutPlan{}, Rooms: &rooms}).morgueSites(); len(got) != 0 {
		t.Fatalf("no planned morgue, no site: %+v", got)
	}
}

func TestMorgueFilterKeepsColonistsAndRottenCorpsesOut(t *testing.T) {
	f := domain.MorgueCorpsesFilter()
	for _, name := range []string{"AllowRotten", "AllowCorpsesColonist", "AllowCorpsesSlave"} {
		if !containsSelector(f.Disallow(), domain.SpecialFilter(name)) {
			t.Errorf("%s allowed", name)
		}
	}
	if containsSelector(f.Disallow(), domain.SpecialFilter("AllowCorpsesStranger")) {
		t.Error("strangers refused")
	}
	if !containsSelector(domain.TombCorpsesFilter().Disallow(), domain.SpecialFilter("AllowCorpsesStranger")) {
		t.Error("the tomb competes for strangers")
	}
}

func containsSelector(rows []domain.FilterSelector, s domain.FilterSelector) bool {
	for _, r := range rows {
		if r == s {
			return true
		}
	}
	return false
}

func containsRole(roles []PlannedRole, r PlannedRole) bool {
	for _, x := range roles {
		if x == r {
			return true
		}
	}
	return false
}
