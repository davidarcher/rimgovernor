package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A planned 3x3 room at interior (10,10): ground is (9,9) 5x5.
func groundFixture() (LayoutPlan, RoomObservation) {
	plan := LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedBedroom, Interior: Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, Door: domain.Cell{X: 11, Z: 9}, DoorRot: domain.South}}}
	return plan, RoomObservation{Shapes: testShapes}
}

func playerRow(id, def, class string, min, max domain.Cell, encloses bool) ClearanceTarget {
	return ClearanceTarget{EntityID: id, DefName: def, Class: class, Minimum: min, Maximum: max, Deconstructible: true, InHome: true, Player: true, EnclosesRoom: encloses}
}

func TestPlannedGroundSkipsStandingRooms(t *testing.T) {
	plan, _ := groundFixture()
	if got := PlannedGround(plan, GroundCensus{}); !reflect.DeepEqual(got, []Rectangle{{X: 9, Z: 9, Width: 5, Height: 5}}) {
		t.Fatalf("ground = %v", got)
	}
	if got := PlannedGround(plan, ringWalls(plan, plan.Rooms[0])); len(got) != 0 {
		t.Fatalf("a standing room needs no ground: %v", got)
	}
}

func TestPlannedGroundTargetsUnplannedOnly(t *testing.T) {
	plan, _ := groundFixture()
	ground := PlannedGround(plan, GroundCensus{})
	rows := []ClearanceTarget{
		playerRow("bed", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false),
		// A wall on the planned ring is the room's own wall.
		playerRow("ring", "Wall", "ancient_wall_door", domain.Cell{X: 9, Z: 11}, domain.Cell{X: 9, Z: 11}, true),
		// A wall inside the interior is not.
		playerRow("inner", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true),
		// A frame is its builder's.
		playerRow("frame", "Frame_Bed", "other", domain.Cell{X: 12, Z: 12}, domain.Cell{X: 12, Z: 12}, false),
		// A door on the ring at the planned door stays; one elsewhere is swapped.
		playerRow("door", "Door", "ancient_wall_door", domain.Cell{X: 11, Z: 9}, domain.Cell{X: 11, Z: 9}, true),
		playerRow("swap", "Door", "ancient_wall_door", domain.Cell{X: 13, Z: 10}, domain.Cell{X: 13, Z: 10}, true),
		// Off the ground.
		playerRow("far", "Table", "other", domain.Cell{X: 30, Z: 30}, domain.Cell{X: 30, Z: 30}, false),
	}
	floors := []ClearanceFloor{{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}, {Cell: domain.Cell{X: 12, Z: 10}, DefName: "WoodPlankFloor"}, {Cell: domain.Cell{X: 9, Z: 11}, DefName: "WoodPlankFloor"}}
	got := PlannedGroundWork(rows, floors, ground, PlannedDoors(plan), RetiredGroundOf(plan))
	// The floor under the bed waits for it; the one under the ring wall stays.
	want := []string{"bed", GroundFloorID(domain.Cell{X: 12, Z: 10}), "inner", "swap"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("work = %v, want %v", got, want)
	}
}

func TestPlannedGroundStepOrder(t *testing.T) {
	plan, rooms := groundFixture()
	ground := PlannedGround(plan, GroundCensus{})
	bed := playerRow("bed", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	wall := playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true)
	floors := []ClearanceFloor{{Cell: domain.Cell{X: 12, Z: 12}, DefName: "WoodPlankFloor"}}
	rooms.Rooms = []Room{
		{ID: "inside", Enclosed: domain.Known(true), Cells: []domain.Cell{{X: 12, Z: 11}, {X: 12, Z: 12}}},
		{ID: "straddles", Enclosed: domain.Known(true), Cells: []domain.Cell{{X: 11, Z: 12}, {X: 11, Z: 20}}},
	}

	doors := PlannedDoors(plan)
	swap := playerRow("swap", "Door", "ancient_wall_door", domain.Cell{X: 9, Z: 10}, domain.Cell{X: 9, Z: 10}, true)
	step, ok := PlannedGroundStep([]ClearanceTarget{wall, swap, bed}, floors, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 1 || step.Targets[0].EntityID != "bed" {
		t.Fatalf("furniture first: %+v", step)
	}
	step, ok = PlannedGroundStep([]ClearanceTarget{wall, swap}, floors, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundDoors || len(step.Targets) != 1 || step.Targets[0].EntityID != "swap" {
		t.Fatalf("ring door swap after furniture: %+v", step)
	}
	step, ok = PlannedGroundStep([]ClearanceTarget{wall}, floors, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundWalls || step.Targets[0].EntityID != "wall" {
		t.Fatalf("walls after furniture: %+v", step)
	}
	if want := []domain.Cell{{X: 12, Z: 11}, {X: 12, Z: 12}}; !reflect.DeepEqual(step.Roof, want) {
		t.Fatalf("roof = %v, want only the room inside the ground %v", step.Roof, want)
	}
	step, ok = PlannedGroundStep(nil, floors, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundFloors || len(step.Floors) != 1 {
		t.Fatalf("floors last: %+v", step)
	}
	if _, ok = PlannedGroundStep(nil, nil, ground, doors, rooms, RetiredGround{}); ok {
		t.Fatal("clear ground has no step")
	}
}

// Packable furniture is packed in one batch, in-use pieces last; a conduit and
// a wall stay deconstructions; a Deconstruct-designated packable piece is
// adopted as a deconstruction, never given an uninstall (#2103).
func TestPlannedGroundStepPacksPackableFurniture(t *testing.T) {
	plan, rooms := groundFixture()
	ground := PlannedGround(plan, rooms)
	doors := PlannedDoors(plan)
	piece := func(id, def string, x, z int32, inUse bool) ClearanceTarget {
		row := playerRow(id, def, "other", domain.Cell{X: x, Z: z}, domain.Cell{X: x, Z: z}, false)
		row.Packable, row.InUse = true, inUse
		return row
	}
	bed := piece("a-bed", "Bed", 10, 10, true)
	table := piece("b-table", "Table2x2c", 11, 10, false)
	lamp := piece("c-lamp", "StandingLamp", 12, 10, false)
	step, ok := PlannedGroundStep([]ClearanceTarget{bed, table, lamp}, nil, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundPack || len(step.Targets) != 3 || step.Targets[2].EntityID != "a-bed" || step.Targets[0].EntityID != "b-table" || step.Targets[1].EntityID != "c-lamp" {
		t.Fatalf("one batch, in-use bed last: %+v", step)
	}
	conduit := playerRow("conduit", "PowerConduit", "other", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, false)
	step, ok = PlannedGroundStep([]ClearanceTarget{bed, table, conduit}, nil, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 1 || step.Targets[0].EntityID != "conduit" {
		t.Fatalf("a conduit is deconstructed before the packing: %+v", step)
	}
	wall := playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true)
	wall.Packable = true
	if step, ok = PlannedGroundStep([]ClearanceTarget{wall}, nil, ground, doors, rooms, RetiredGround{}); !ok || step.Phase != GroundWalls {
		t.Fatalf("a wall is never packed: %+v", step)
	}
	table.Designated = true
	step, ok = PlannedGroundStep([]ClearanceTarget{table}, nil, ground, doors, rooms, RetiredGround{})
	if !ok || step.Phase != GroundFurniture || step.Targets[0].EntityID != "b-table" {
		t.Fatalf("a Deconstruct-designated piece is not packed: %+v", step)
	}
}

func TestSplitGroundRows(t *testing.T) {
	others, player := SplitGroundRows([]ClearanceTarget{{EntityID: "ruin"}, {EntityID: "mine", Player: true}})
	if len(others) != 1 || others[0].EntityID != "ruin" || len(player) != 1 || player[0].EntityID != "mine" {
		t.Fatalf("split = %v / %v", others, player)
	}
}

// A shelter sleeping spot on planned ground is the colony's only bed until
// the room stands: ground clearance leaves it.
func TestPlannedGroundStepLeavesTheStandInBed(t *testing.T) {
	plan, rooms := groundFixture()
	ground := PlannedGround(plan, GroundCensus{})
	spot := playerRow("spot", "SleepingSpot", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	if step, ok := PlannedGroundStep([]ClearanceTarget{spot}, nil, ground, PlannedDoors(plan), rooms, RetiredGround{}); ok {
		t.Fatalf("cleared the stand-in bed: %+v", step)
	}
	if work := PlannedGroundWork([]ClearanceTarget{spot}, nil, ground, PlannedDoors(plan), RetiredGroundOf(plan)); len(work) != 0 {
		t.Fatal(work)
	}
}

// An outdoor dump keeps off the plan's room ground even before the room
// stands.
func TestDumpProtectedAddsPlannedGround(t *testing.T) {
	plan, _ := groundFixture()
	ground := PlannedRoomGround(plan)
	if len(ground) == 0 {
		t.Fatal("no planned ground")
	}
	room := plan.AllRooms()[0].Interior
	cell := domain.Cell{X: room.X, Z: room.Z}
	blocked := outdoorDumpBlocked(nil, dumpProtected(nil, ground))
	if !blocked[cell] {
		t.Fatal("dump may take planned room ground", cell)
	}
}
