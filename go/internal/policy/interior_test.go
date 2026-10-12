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

func TestBedroomPlansAcrossDoorsAndSizes(t *testing.T) {
	for _, size := range [][3]int32{{5, 4, 0}, {5, 4, 1}, {4, 4, 1}, {3, 3, 1}, {6, 5, 2}} {
		for _, room := range interiorRoomsAround(RoomRoleBedroom, size[0], size[1], size[2]) {
			if _, ok := PlanRoomInterior(room, InteriorPieceDef{}); !ok {
				t.Errorf("%v door %v: no plan", size, room.Doors)
			}
		}
	}
}

func TestPlanRoomInteriorRefusesUnfitRooms(t *testing.T) {
	doorless := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 4}}
	if _, ok := PlanRoomInterior(doorless, InteriorPieceDef{}); ok {
		t.Error("a doorless room planned")
	}
	unfurnished := InteriorRoom{Shapes: testShapes, Role: RoomRole("Untemplated"), Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 4}, Doors: []domain.Cell{{X: 1, Z: -1}}}
	if _, ok := PlanRoomInterior(unfurnished, InteriorPieceDef{}); ok {
		t.Error("a role with no wants planned")
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
	room, ok := InteriorRoomFromLayout(PlannedRoom{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 4}), Door: domain.Cell{X: 1, Z: -1}}, testShapes)
	if !ok || room.Role != RoomRoleBedroom {
		t.Fatalf("room %+v %v", room, ok)
	}
	if _, ok := PlanRoomInterior(room, InteriorPieceDef{}); !ok {
		t.Error("a v2 bedroom does not plan")
	}
}

func TestRowCapacity(t *testing.T) {
	if RowCapacity(10, 3, 1) != 2 || RowCapacity(11, 3, 1) != 3 {
		t.Error("row capacity")
	}
}
