package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bridgedHallways are two parallel hallways with no other link; the entrance
// opens onto the lower one. A room between them with a door on each side is
// the only way across.
func bridgedHallways(bridge PlannedRole) LayoutPlan {
	lower := []SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 30, Z: 0}}}
	upper := SpineSegment{From: domain.Cell{X: 0, Z: 12}, To: domain.Cell{X: 30, Z: 12}}
	return LayoutPlan{
		Spine:     append(lower, upper),
		Entrances: spineEntrances(lower),
		Rooms: []PlannedRoom{
			{Role: bridge, Interior: Rectangle{X: 10, Z: 3, Width: 5, Height: 7},
				Door: domain.Cell{X: 12, Z: 2}, DoorRot: domain.South,
				Doors: []Door{{Cell: domain.Cell{X: 12, Z: 10}, Rot: domain.North}}},
			{Role: PlannedStorage, Interior: Rectangle{X: 20, Z: 14, Width: 5, Height: 5},
				Door: domain.Cell{X: 22, Z: 13}, DoorRot: domain.South},
		},
	}
}

func TestCheckRoutesRingBridgedByPassThroughRoom(t *testing.T) {
	traffic, err := CheckRoutes(bridgedHallways(PlannedDining))
	if err != nil {
		t.Fatal(err)
	}
	if traffic[domain.Cell{X: 12, Z: 10}] == 0 {
		t.Fatal("the entrance trip does not use the second door")
	}
}

func TestCheckRoutesRingBridgedByBedroomIsThoroughfare(t *testing.T) {
	_, err := CheckRoutes(bridgedHallways(PlannedBedroom))
	if err == nil || !strings.Contains(err.Error(), "crosses bedroom") {
		t.Fatal("a bedroom bridge passed", err)
	}
}

func TestCheckRoutesWithoutSecondDoorIsUnreachable(t *testing.T) {
	p := bridgedHallways(PlannedDining)
	p.Rooms[0].Doors = nil
	if _, err := CheckRoutes(p); err == nil || !strings.Contains(err.Error(), "no route") {
		t.Fatal("storage reached without the second door", err)
	}
}

func TestCheckRoutesTripsConsiderEveryRoomOfTheTargetRole(t *testing.T) {
	// The first storage sits behind a bedroom; a second one opens onto the
	// hallway, so the entrance trip takes it and no thoroughfare is hit.
	p := LayoutPlan{
		Spine:     []SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}},
		Entrances: spineEntrances([]SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}}),
		Rooms: []PlannedRoom{
			{Role: PlannedBedroom, Interior: Rectangle{X: 0, Z: 3, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: 2}, DoorRot: domain.South},
			{Role: PlannedStorage, Interior: Rectangle{X: 0, Z: 20, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: 19}, DoorRot: domain.South}, // walled off: no hallway or room touches its door
			{Role: PlannedStorage, Interior: Rectangle{X: 8, Z: 3, Width: 5, Height: 5}, Door: domain.Cell{X: 10, Z: 2}, DoorRot: domain.South},
		},
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
}

func TestInteriorRoomFromLayoutKeepsExtraDoors(t *testing.T) {
	r := bridgedHallways(PlannedDining).Rooms[0]
	in, ok := InteriorRoomFromLayout(r, testShapes)
	in.Dining = testDining
	if !ok || len(in.Doors) != 2 {
		t.Fatalf("doors %+v %v", in.Doors, ok)
	}
	plan, ok := PlanInterior(in, InteriorPieceDef{})
	if !ok {
		t.Fatal("a two-door dining room does not plan")
	}
	doors := map[domain.Cell]bool{}
	for _, d := range in.Doors {
		doors[d] = true
	}
	// No piece stands on the cell just inside any door.
	inside := map[domain.Cell]bool{}
	for d := range doors {
		switch side, _ := doorSide(r.Interior, d); side {
		case domain.South:
			inside[domain.Cell{X: d.X, Z: d.Z + 1}] = true
		case domain.North:
			inside[domain.Cell{X: d.X, Z: d.Z - 1}] = true
		}
	}
	for _, pc := range plan.Pieces {
		for _, c := range RectangleCells(pc.Rect) {
			if inside[c] {
				t.Fatalf("piece %s blocks a door at %v", pc.Slot, c)
			}
		}
	}
}

func TestRoomRockDigsEveryDoorAndThreshold(t *testing.T) {
	p := bridgedHallways(PlannedDining)
	room := p.Rooms[0]
	room.Dug = true
	dig := map[domain.Cell]bool{}
	for _, c := range p.RoomRock(room, nil).Dig {
		dig[c] = true
	}
	for _, c := range []domain.Cell{room.Door, room.Doors[0].Cell, {X: 12, Z: 1}, {X: 12, Z: 11}} {
		if !dig[c] {
			t.Fatalf("%v not dug", c)
		}
	}
}

func TestOverlayDrawsEveryDoor(t *testing.T) {
	o := bridgedHallways(PlannedDining).Overlay(Bounds{Width: 60, Height: 60})
	for _, l := range o.Layers {
		if l.Label != "door" {
			continue
		}
		n := int32(0)
		for _, r := range l.Runs {
			n += r.Length
		}
		if n != 3 {
			t.Fatalf("door cells %d, want 3", n)
		}
		return
	}
	t.Fatal("no door layer")
}
