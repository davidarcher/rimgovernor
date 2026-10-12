package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterInterior(width, depth int32, occupants int) InteriorRoom {
	room := interiorRoomsAround(RoomRoleShelter, width, depth, width/2-1)[0]
	room.Occupants, room.Campfires, room.Capabilities = occupants, ShelterCampfires(true), CapabilitiesOf(PlannedShelter)
	return room
}

func slotsOf(plan InteriorPlan, prefix string) []InteriorPiece {
	var out []InteriorPiece
	for _, p := range plan.Pieces {
		if strings.HasPrefix(p.Slot, prefix) {
			out = append(out, p)
		}
	}
	return out
}

func TestShelterPlansAcrossSizes(t *testing.T) {
	for _, size := range [][3]int32{{7, 5, 2}, {6, 4, 1}, {9, 9, 3}, {5, 4, 1}, {8, 6, 3}} {
		_, plan := planAround(t, RoomRoleShelter, size[0], size[1], size[2], InteriorPieceDef{})
		if len(slotsOf(plan, "research")) != 1 || len(slotsOf(plan, "craft")) != 1 || len(slotsOf(plan, "campfire.")) != ShelterCampfires(true) {
			t.Errorf("%dx%d: pieces %+v miss the research table, crafting spot or campfire", size[0], size[1], plan.Pieces)
		}
	}
}

func TestShelterResearchTableKeepsItsFrontClear(t *testing.T) {
	plan, ok := PlanRoomInterior(shelterInterior(7, 5, 3), InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	table := slotsOf(plan, "research")[0]
	if table.Def != testResearch || table.Rect.Width != 3 || table.Rect.Height != 2 {
		t.Fatalf("table %+v", table)
	}
	front, ok := table.Interaction()
	if !ok {
		t.Fatal("no interaction cell")
	}
	for _, p := range plan.Pieces {
		for _, c := range rectCells(p.Rect) {
			if c == front {
				t.Fatalf("%s stands on the table's front cell %v", p.Slot, c)
			}
		}
	}
}

func TestShelterBunkSlotsFollowOccupants(t *testing.T) {
	for n := 1; n <= 6; n++ {
		plan, ok := PlanRoomInterior(shelterInterior(7, 5, n), InteriorPieceDef{})
		if !ok {
			t.Fatalf("%d occupants: no plan", n)
		}
		if got := len(slotsOf(plan, "bunk.")); got != n {
			t.Errorf("%d occupants: %d bunks", n, got)
		}
		for _, p := range slotsOf(plan, "bunk.") {
			if p.Size != (domain.Cell{X: 1, Z: 2}) || !p.IsBunk() {
				t.Errorf("bunk %+v", p)
			}
		}
	}
	full, _ := PlanRoomInterior(shelterInterior(7, 5, 0), InteriorPieceDef{})
	if len(slotsOf(full, "bunk.")) <= 6 {
		t.Errorf("0 occupants fills only %d bunks", len(slotsOf(full, "bunk.")))
	}
	// More occupants than room: whatever fits, never an error.
	crowded, ok := PlanRoomInterior(shelterInterior(7, 5, 40), InteriorPieceDef{})
	if !ok || len(slotsOf(crowded, "bunk.")) != len(slotsOf(full, "bunk.")) {
		t.Errorf("40 occupants: %d bunks, want %d (ok=%v)", len(slotsOf(crowded, "bunk.")), len(slotsOf(full, "bunk.")), ok)
	}
}

func TestShelterBunksKeepOffDoorAndReservedCells(t *testing.T) {
	room := shelterInterior(9, 7, 0)
	reserved := Rectangle{X: room.Interior.X + 3, Z: room.Interior.Z + 2, Width: 3, Height: 3}
	room.Reserved = rectCells(reserved)
	plan, ok := PlanRoomInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	inside := interiorThresholds(room)[0]
	holds := map[domain.Cell]bool{}
	for _, c := range room.Reserved {
		holds[c] = true
	}
	for _, p := range slotsOf(plan, "bunk.") {
		for _, c := range rectCells(p.Rect) {
			if holds[c] || c == inside {
				t.Fatalf("bunk %s cell %v: reserved=%v door aisle=%v", p.Slot, c, holds[c], c == inside)
			}
		}
	}
	if fire := slotsOf(plan, "campfire."); len(fire) != ShelterCampfires(true) {
		t.Errorf("campfire %+v", fire)
	}
}

func TestShelterBunkRectsFollowTheirAnchors(t *testing.T) {
	room := InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Doors: []domain.Cell{{X: 12, Z: 19}}, Occupants: 2, Capabilities: CapabilitiesOf(PlannedShelter)}
	plan, ok := PlanRoomInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	for _, p := range slotsOf(plan, "bunk.") {
		if got := BunkRect(p.Anchor(), p.Rot); got != p.Rect {
			t.Errorf("BunkRect %+v, piece %+v", got, p.Rect)
		}
	}
}

func TestShelterKeepsBunksWhenTheRoomIsCramped(t *testing.T) {
	// Too shallow for the research table: bunks alone, no refusal.
	plan, ok := PlanRoomInterior(shelterInterior(6, 3, 2), InteriorPieceDef{})
	if !ok || len(slotsOf(plan, "research")) != 0 || len(slotsOf(plan, "bunk.")) != 2 {
		t.Fatalf("plan %+v ok=%v", plan.Pieces, ok)
	}
}

// A shelter plans a piece only for a capability it hosts.
func TestShelterPlansOnlyHostedCapabilities(t *testing.T) {
	room := shelterInterior(7, 5, 2)
	room.Capabilities = []Capability{CapSleep}
	plan, ok := PlanRoomInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	for _, prefix := range []string{"research", "craft", "campfire."} {
		if n := len(slotsOf(plan, prefix)); n != 0 {
			t.Errorf("%s: %d slots without the capability, want 0", prefix, n)
		}
	}
	if len(slotsOf(plan, shelterBunkSlotPrefix)) == 0 {
		t.Error("no bunks")
	}
}

func TestShelterSizesLeaveTheResearchTableItsDepth(t *testing.T) {
	for n := 1; n < 12; n++ {
		for _, s := range ShelterSizes(n, ShelterCampfires(true), 0) {
			if s[1] < 4 {
				t.Fatalf("%d colonists: %v is shallower than the 3x2 table and its front row", n, s)
			}
			plan, ok := PlanRoomInterior(InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: Rectangle{Width: s[0], Height: s[1]}, Doors: []domain.Cell{{X: s[0] / 2, Z: -1}}, Occupants: n, Campfires: ShelterCampfires(true), Capabilities: CapabilitiesOf(PlannedShelter)}, InteriorPieceDef{})
			if !ok || len(slotsOf(plan, "research")) != 1 || len(slotsOf(plan, "craft")) != 1 || len(slotsOf(plan, "campfire.")) != ShelterCampfires(true) {
				t.Fatalf("%d colonists: %v lays out %+v (ok=%v)", n, s, plan.Pieces, ok)
			}
		}
	}
}
