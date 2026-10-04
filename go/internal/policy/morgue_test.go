package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func morgueFixture() (LayoutPlan, LayoutRoom) {
	room := LayoutRoom{Role: ModuleMorgue, Interior: Rectangle{X: 10, Z: 20, Width: 5, Height: 4}, Door: domain.Cell{X: 12, Z: 19}, DoorRot: domain.North}
	return LayoutPlan{Rooms: []LayoutRoom{room}}, room
}

func TestMorgueShelledForAFreshStrangerOnlyWhileButcheryIsOpen(t *testing.T) {
	plan, room := morgueFixture()
	fresh := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger, RotStage: domain.RotFresh}}
	if got, owed := MorgueRoomOwed(plan, RoomObservation{Shapes: testShapes}, fresh, true); !owed || !got.Same(room) {
		t.Fatalf("fresh stranger: %+v %v", got, owed)
	}
	if _, owed := MorgueRoomOwed(plan, RoomObservation{Shapes: testShapes}, fresh, false); owed {
		t.Fatal("butchery closed: the stranger is burned, not kept")
	}
	rotten := []WasteItem{{ID: "Corpse_2", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger, RotStage: domain.RotRotting}}
	colonist := []WasteItem{{ID: "Corpse_3", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist, RotStage: domain.RotFresh}}
	for _, waste := range [][]WasteItem{rotten, colonist, nil} {
		if _, owed := MorgueRoomOwed(plan, RoomObservation{Shapes: testShapes}, waste, true); owed {
			t.Fatalf("owed for %+v", waste)
		}
	}
	standing := tombStanding(LayoutRoom{Interior: room.Interior})
	if _, owed := MorgueRoomOwed(plan, standing, fresh, true); owed {
		t.Fatal("a standing morgue owes no shell")
	}
}

func TestMorgueIsCooledOnceStanding(t *testing.T) {
	plan, room := morgueFixture()
	rooms := tombStanding(LayoutRoom{Interior: room.Interior})
	rooms.Rooms[0].Temperature = domain.Known(18.0)
	built := domain.Known(CurrentConstruction{Colony: true})
	none := domain.Known([]WasteItem{})
	if got, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), none, built).Value(); !known || !reflect.DeepEqual(got, []string{"r1"}) {
		t.Fatalf("a standing warm morgue owes cooling: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Known(-6.0)
	if got, _ := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), none, built).Value(); len(got) != 0 {
		t.Fatalf("a frozen morgue is done: %v", got)
	}
}

func TestMorgueSiteHoldsFreshStrangersAheadOfTheDump(t *testing.T) {
	plan, room := morgueFixture()
	rooms := tombStanding(LayoutRoom{Interior: room.Interior})
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

func TestCoreGrowPlansAMorgueBesideTheTomb(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	var morgue, tomb *LayoutRoom
	for i, r := range p.Rooms {
		switch r.Role {
		case ModuleMorgue:
			morgue = &p.Rooms[i]
		case ModuleTomb:
			tomb = &p.Rooms[i]
		}
	}
	if morgue == nil || tomb == nil {
		t.Fatal("morgue or tomb not planned")
	}
	if cold := coolingRoles; !containsRole(cold, ModuleMorgue) {
		t.Fatal("morgue not a cooled role")
	}
}

func containsRole(roles []ModuleRole, r ModuleRole) bool {
	for _, x := range roles {
		if x == r {
			return true
		}
	}
	return false
}
