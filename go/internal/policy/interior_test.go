package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestOccupiedRectMatchesRimWorld(t *testing.T) {
	bed := domain.Cell{X: 1, Z: 2}
	a := domain.Cell{X: 5, Z: 5}
	for rot, want := range map[domain.Rotation]Rectangle{
		domain.North: {X: 5, Z: 5, Width: 1, Height: 2},
		domain.East:  {X: 5, Z: 5, Width: 2, Height: 1},
		domain.South: {X: 5, Z: 4, Width: 1, Height: 2},
		domain.West:  {X: 4, Z: 5, Width: 2, Height: 1},
	} {
		if got := OccupiedRect(a, bed, rot); got != want {
			t.Errorf("bed %s: %+v, want %+v", rot, got, want)
		}
	}
	for _, size := range []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 2}, {X: 3, Z: 1}, {X: 2, Z: 2}, {X: 3, Z: 2}} {
		for _, rot := range rotationOrder {
			r := OccupiedRect(a, size, rot)
			if got := AnchorForRect(r, size, rot); got != a {
				t.Errorf("size %+v %s: anchor %+v from %+v, want %+v", size, rot, got, r, a)
			}
		}
	}
}

func TestBedroomLayoutRepeatsAcrossDoorsAndSizes(t *testing.T) {
	for _, size := range [][3]int32{{5, 4, 0}, {5, 4, 1}, {4, 4, 1}, {3, 3, 1}, {6, 5, 2}} {
		assertInteriorRepeatable(t, RoomRoleBedroom, size[0], size[1], size[2])
	}
}

func TestBedroomBedHeadAgainstTheFarWall(t *testing.T) {
	room := Rectangle{X: 10, Z: 20, Width: 5, Height: 4}
	cases := []struct {
		door   domain.Cell
		rect   Rectangle
		rot    domain.Rotation
		anchor domain.Cell
	}{
		// The anchor is the head cell (BedUtility.GetSleepingSlotPos): on
		// the wall opposite the door, feet toward the door.
		{domain.Cell{X: 12, Z: 19}, Rectangle{X: 12, Z: 22, Width: 1, Height: 2}, domain.South, domain.Cell{X: 12, Z: 23}},
		{domain.Cell{X: 12, Z: 24}, Rectangle{X: 12, Z: 20, Width: 1, Height: 2}, domain.North, domain.Cell{X: 12, Z: 20}},
		{domain.Cell{X: 9, Z: 21}, Rectangle{X: 13, Z: 21, Width: 2, Height: 1}, domain.West, domain.Cell{X: 14, Z: 21}},
	}
	for _, c := range cases {
		plan, ok := PlanInterior(InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: room, Doors: []domain.Cell{c.door}}, InteriorPieceDef{})
		if !ok || len(plan.Pieces) == 0 || plan.Pieces[0].Slot != "bed" {
			t.Fatalf("door %+v: plan %+v %v", c.door, plan, ok)
		}
		bed := plan.Pieces[0]
		if bed.Rect != c.rect || bed.Rot != c.rot || bed.Anchor() != c.anchor {
			t.Errorf("door %+v: bed %+v %s at %+v, want %+v %s at %+v", c.door, bed.Rect, bed.Rot, bed.Anchor(), c.rect, c.rot, c.anchor)
		}
	}
}

func TestPlanInteriorRefusesUnfitRooms(t *testing.T) {
	shallow := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 2}, Doors: []domain.Cell{{X: 1, Z: -1}}}
	if _, ok := PlanInterior(shallow, InteriorPieceDef{}); ok {
		t.Error("a two-deep room fits the bedroom template")
	}
	doorless := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 4}}
	if _, ok := PlanInterior(doorless, InteriorPieceDef{}); ok {
		t.Error("a doorless room planned")
	}
	untemplated := InteriorRoom{Shapes: testShapes, Role: RoomRole("Untemplated"), Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 4}, Doors: []domain.Cell{{X: 1, Z: -1}}}
	if _, ok := PlanInterior(untemplated, InteriorPieceDef{}); ok {
		t.Error("a role without a template planned")
	}
}

func TestInteriorRoomFromCensus(t *testing.T) {
	var cells []domain.Cell
	for x := int32(0); x < 3; x++ {
		for z := int32(0); z < 4; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	doors := []domain.Cell{{X: 3, Z: 2}, {X: -1, Z: -1}, {X: 7, Z: 7}}
	room, ok := InteriorRoomFromCensus(Room{Cells: cells}, RoomRoleBedroom, doors, testShapes)
	if !ok || room.Interior != (Rectangle{X: 0, Z: 0, Width: 3, Height: 4}) || len(room.Doors) != 1 || room.Doors[0] != (domain.Cell{X: 3, Z: 2}) {
		t.Fatalf("room %+v %v", room, ok)
	}
	if _, ok := InteriorRoomFromCensus(Room{Cells: cells[1:]}, RoomRoleBedroom, doors, testShapes); ok {
		t.Error("a non-rectangular room has an interior")
	}
}

func TestInteriorRoomFromLayout(t *testing.T) {
	room, ok := InteriorRoomFromLayout(LayoutRoom{Role: ModuleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 4, Height: 4}, Door: domain.Cell{X: 1, Z: -1}}, testShapes)
	if !ok || room.Role != RoomRoleBedroom {
		t.Fatalf("room %+v %v", room, ok)
	}
	if _, ok := PlanInterior(room, InteriorPieceDef{}); !ok {
		t.Error("a v2 bedroom does not plan")
	}
}

func TestRowHelpersAndRegularityChecker(t *testing.T) {
	starts, ok := RowStarts(9, 3, 0, 3, RowCentred)
	if !ok || starts[0] != 0 || starts[2] != 6 {
		t.Fatalf("starts %v", starts)
	}
	if RowCapacity(10, 3, 1) != 2 || RowCapacity(11, 3, 1) != 3 {
		t.Error("row capacity")
	}
	f := InteriorFrame{Shapes: testShapes, Width: 9, Depth: 5, Doors: []domain.Cell{{X: 4, Z: -1}}, Entrance: 4}
	var pieces []InteriorPiece
	starts, _ = RowStarts(f.Width, 3, 0, 2, RowCentred)
	for i, u := range starts {
		p := NewInteriorPiece("bench."+string(rune('a'+i)), "TableStonecutter", domain.Cell{X: 3, Z: 1}, domain.North, domain.Cell{X: u, Z: 4})
		p.Row = "back"
		pieces = append(pieces, p)
	}
	left := NewInteriorPiece("shelf.l", "Shelf", domain.Cell{X: 1, Z: 2}, domain.East, domain.Cell{X: 0, Z: 1})
	left.Pair = "shelves"
	pieces = append(pieces, left, f.MirrorPiece(left, "shelf.r"))
	plan := InteriorPlan{Frame: f, Canonical: pieces}
	if v := interiorRegularityViolations(plan); len(v) != 0 {
		t.Errorf("regular plan flagged: %v", v)
	}
	pieces[1].Rect.Z = 3
	pieces[3].Rect.X--
	if v := interiorRegularityViolations(InteriorPlan{Frame: f, Canonical: pieces}); len(v) != 2 {
		t.Errorf("violations %v, want the row off its line and the broken pair", v)
	}
}
