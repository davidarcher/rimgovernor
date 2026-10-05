package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterInterior(width, depth int32, occupants int) InteriorRoom {
	room := interiorRoomsAround(RoomRoleShelter, width, depth, width/2-1)[0]
	room.Occupants, room.Campfires = occupants, ShelterCampfires(true)
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

func TestShelterLayoutRepeatsAcrossDoorsAndSizes(t *testing.T) {
	for _, size := range [][3]int32{{7, 5, 2}, {6, 4, 1}, {9, 9, 3}, {5, 4, 1}, {8, 6, 3}} {
		plan := assertInteriorRepeatable(t, RoomRoleShelter, size[0], size[1], size[2])
		if len(slotsOf(plan, "research")) != 1 || len(slotsOf(plan, "craft")) != 1 || len(slotsOf(plan, "campfire.")) != ShelterCampfires(true) {
			t.Errorf("%dx%d: pieces %+v miss the research table, crafting spot or campfire", size[0], size[1], plan.Canonical)
		}
	}
}

func TestShelterResearchTableCentredOnTheBackWallWithItsFrontClear(t *testing.T) {
	plan, ok := PlanInterior(shelterInterior(7, 5, 3), InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	table := slotsOf(plan, "research")[0]
	canonical := plan.Canonical[0]
	if table.Def != testResearch || canonical.Slot != "research" || canonical.Rect != (Rectangle{X: 2, Z: 3, Width: 3, Height: 2}) {
		t.Fatalf("table %+v canonical %+v", table, canonical)
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
		plan, ok := PlanInterior(shelterInterior(7, 5, n), InteriorPieceDef{})
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
	full, _ := PlanInterior(shelterInterior(7, 5, 0), InteriorPieceDef{})
	if len(slotsOf(full, "bunk.")) <= 6 {
		t.Errorf("0 occupants fills only %d bunks", len(slotsOf(full, "bunk.")))
	}
	// More occupants than room: whatever fits, never an error.
	crowded, ok := PlanInterior(shelterInterior(7, 5, 40), InteriorPieceDef{})
	if !ok || len(slotsOf(crowded, "bunk.")) != len(slotsOf(full, "bunk.")) {
		t.Errorf("40 occupants: %d bunks, want %d (ok=%v)", len(slotsOf(crowded, "bunk.")), len(slotsOf(full, "bunk.")), ok)
	}
}

func TestShelterBunksShareOneRotation(t *testing.T) {
	// Every room has one bunk rotation, east-west only where it beds more
	// colonists than north-south.
	var east int
	for width := int32(5); width <= 9; width++ {
		for depth := int32(4); depth <= 7; depth++ {
			plan, ok := PlanInterior(shelterInterior(width, depth, 0), InteriorPieceDef{})
			if !ok {
				t.Fatalf("%dx%d: no plan", width, depth)
			}
			bunks := slotsOf(plan, "bunk.")
			for _, p := range bunks {
				if p.Rot != bunks[0].Rot {
					t.Fatalf("%dx%d: bunks mix %s and %s", width, depth, bunks[0].Rot, p.Rot)
				}
			}
			if len(bunks) > 0 && bunks[0].Rot == domain.East {
				east++
				if bunks[0].Rect.Width != 2 || bunks[0].Rect.Height != 1 {
					t.Errorf("%dx%d: east bunk %+v", width, depth, bunks[0].Rect)
				}
			}
		}
	}
	t.Logf("%d of the sizes lay east-west", east)
}

func TestShelterBunksKeepOffDoorAndReservedCells(t *testing.T) {
	room := shelterInterior(9, 7, 0)
	reserved := Rectangle{X: room.Interior.X + 3, Z: room.Interior.Z + 2, Width: 3, Height: 3}
	room.Reserved = rectCells(reserved)
	plan, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	corners := map[domain.Cell]bool{}
	r := room.Interior
	for _, x := range []int32{r.X, r.X + r.Width - 1} {
		for _, z := range []int32{r.Z, r.Z + r.Height - 1} {
			corners[domain.Cell{X: x, Z: z}] = true
		}
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
	// A campfire may stand in a corner, beside bunks.
	if fire := slotsOf(plan, "campfire."); len(fire) != ShelterCampfires(true) || !corners[domain.Cell{X: fire[0].Rect.X, Z: fire[0].Rect.Z}] {
		t.Errorf("campfire %+v", fire)
	}
}

func TestShelterHeadsPointAwayFromTheDoor(t *testing.T) {
	// Door on the south wall: a South-facing bunk anchors on its higher (far)
	// cell, so the head lies away from the door.
	room := InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Doors: []domain.Cell{{X: 12, Z: 19}}, Occupants: 2}
	plan, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	for _, p := range slotsOf(plan, "bunk.") {
		if p.Rot != domain.South || p.Anchor().Z != p.Rect.Z+1 {
			t.Errorf("bunk %+v %s anchors at %v", p.Rect, p.Rot, p.Anchor())
		}
		if got := BunkRect(p.Anchor(), p.Rot); got != p.Rect {
			t.Errorf("BunkRect %+v, piece %+v", got, p.Rect)
		}
	}
}

func TestShelterTemplateKeepsBunksWhenTheRoomIsCramped(t *testing.T) {
	// Too shallow for the research table: bunks alone, no refusal.
	plan, ok := PlanInterior(shelterInterior(6, 3, 2), InteriorPieceDef{})
	if !ok || len(slotsOf(plan, "research")) != 0 || len(slotsOf(plan, "bunk.")) != 2 {
		t.Fatalf("plan %+v ok=%v", plan.Pieces, ok)
	}
}

func TestShelterSizesLeaveTheResearchTableItsDepth(t *testing.T) {
	for n := 1; n < 12; n++ {
		for _, s := range ShelterSizes(n, ShelterCampfires(true), 0) {
			if s[1] < 4 {
				t.Fatalf("%d colonists: %v is shallower than the 3x2 table and its front row", n, s)
			}
			plan, ok := PlanInterior(InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: Rectangle{Width: s[0], Height: s[1]}, Doors: []domain.Cell{{X: s[0] / 2, Z: -1}}, Occupants: n, Campfires: ShelterCampfires(true)}, InteriorPieceDef{})
			if !ok || len(slotsOf(plan, "research")) != 1 || len(slotsOf(plan, "craft")) != 1 || len(slotsOf(plan, "campfire.")) != ShelterCampfires(true) {
				t.Fatalf("%d colonists: %v lays out %+v (ok=%v)", n, s, plan.Pieces, ok)
			}
		}
	}
}
