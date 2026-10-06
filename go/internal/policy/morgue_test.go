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

func TestMorgueStoreHoldsEveryHumanCorpseBelowGraves(t *testing.T) {
	plan, room := morgueFixture()
	stores := burialOwner{}.Stores(StoreView{Layout: &plan})
	if len(stores) != 1 || stores[0].Role != domain.MorgueRolePrefix+"10_20" || stores[0].Interior != room.Interior || stores[0].Filter != domain.MorgueCorpsesFilter() {
		t.Fatalf("stores %+v", stores)
	}
	if stores[0].Priority != MorguePriority || MorguePriority == domain.CriticalPriority {
		t.Fatalf("priority %v", stores[0].Priority)
	}
	if got := (burialOwner{}).Stores(StoreView{Layout: &LayoutPlan{}}); len(got) != 0 {
		t.Fatalf("no planned morgue, no store: %+v", got)
	}
}

func TestMorgueFilterHoldsEveryHumanCorpse(t *testing.T) {
	f := domain.MorgueCorpsesFilter()
	if len(f.Disallow()) != 0 {
		t.Errorf("disallows %v", f.Disallow())
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
