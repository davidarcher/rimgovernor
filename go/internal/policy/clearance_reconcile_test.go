package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// gapGround is the fixture room's ring with the cell (9,12) unbuilt, so the
// room's ground does not match the plan and is reconciled.
func gapGround(plan LayoutPlan) GroundCensus {
	g := ringWalls(plan, plan.Rooms[0])
	g.walls[domain.Cell{X: 9, Z: 12}] = false
	return g
}

func TestPlannedGroundWorkOwesWhatTheRingDoesNotHold(t *testing.T) {
	plan, rooms := groundFixture()
	g := ringWalls(plan, plan.Rooms[0])
	// A door on the ring where the plan has none.
	swap := domain.Cell{X: 13, Z: 10}
	g.walls[swap], g.doors[swap] = false, true
	rows := []ClearanceTarget{
		playerRow("bed", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false),
		// A wall inside the interior is not the ring's.
		playerRow("inner", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true),
		// A frame is its builder's: its floor stays.
		playerRow("frame", "Frame_Bed", "other", domain.Cell{X: 12, Z: 12}, domain.Cell{X: 12, Z: 12}, false),
		playerRow("swap", "Door", "ancient_wall_door", swap, swap, true),
		// Off the ground.
		playerRow("far", "Table", "other", domain.Cell{X: 30, Z: 30}, domain.Cell{X: 30, Z: 30}, false),
	}
	floors := []ClearanceFloor{
		{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}, {Cell: domain.Cell{X: 12, Z: 10}, DefName: "WoodPlankFloor"},
		{Cell: domain.Cell{X: 12, Z: 12}, DefName: "WoodPlankFloor"}, {Cell: domain.Cell{X: 9, Z: 11}, DefName: "WoodPlankFloor"},
	}
	got := PlannedGroundWork(plan, g, rows, floors, rooms, RetiredGroundOf(plan))
	want := []string{"bed", GroundFloorID(domain.Cell{X: 10, Z: 10}), GroundFloorID(domain.Cell{X: 12, Z: 10}), "inner", "swap"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("work = %v, want %v", got, want)
	}
}

func TestPlannedGroundStepOrder(t *testing.T) {
	plan, rooms := groundFixture()
	rg := RetiredGroundOf(plan)
	bed := playerRow("bed", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	wall := playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true)
	floors := []ClearanceFloor{{Cell: domain.Cell{X: 12, Z: 12}, DefName: "WoodPlankFloor"}}
	rooms.Rooms = []Room{
		{ID: "inside", Enclosed: domain.Known(true), Cells: []domain.Cell{{X: 12, Z: 11}, {X: 12, Z: 12}}},
		{ID: "straddles", Enclosed: domain.Known(true), Cells: []domain.Cell{{X: 11, Z: 12}, {X: 11, Z: 20}}},
	}
	swapCell := domain.Cell{X: 9, Z: 10}
	swap := playerRow("swap", "Door", "ancient_wall_door", swapCell, swapCell, true)
	swapped := ringWalls(plan, plan.Rooms[0])
	swapped.walls[swapCell], swapped.doors[swapCell] = false, true
	gap := gapGround(plan)

	step, ok := PlannedGroundStep(plan, swapped, []ClearanceTarget{wall, swap, bed}, floors, rooms, rg)
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 1 || step.Targets[0].EntityID != "bed" {
		t.Fatalf("furniture first: %+v", step)
	}
	step, ok = PlannedGroundStep(plan, swapped, []ClearanceTarget{wall, swap}, floors, rooms, rg)
	if !ok || step.Phase != GroundDoors || len(step.Targets) != 1 || step.Targets[0].EntityID != "swap" {
		t.Fatalf("ring door swap after furniture: %+v", step)
	}
	// The roof of the rooms inside the ground comes off before the wall.
	step, ok = PlannedGroundStep(plan, gap, []ClearanceTarget{wall}, floors, rooms, rg)
	if want := []domain.Cell{{X: 12, Z: 11}, {X: 12, Z: 12}}; !ok || step.Phase != GroundWalls || len(step.Targets) != 0 || !reflect.DeepEqual(step.Roof, want) {
		t.Fatalf("roof off before the wall: %+v", step)
	}
	rooms.Rooms = nil
	step, ok = PlannedGroundStep(plan, gap, []ClearanceTarget{wall}, floors, rooms, rg)
	if !ok || step.Phase != GroundWalls || step.Targets[0].EntityID != "wall" || len(step.Cleared) != 1 {
		t.Fatalf("wall on the cleared ground: %+v", step)
	}
	step, ok = PlannedGroundStep(plan, gap, nil, floors, rooms, rg)
	if !ok || step.Phase != GroundFloors || len(step.Floors) != 1 {
		t.Fatalf("floors last: %+v", step)
	}
	if _, ok = PlannedGroundStep(plan, gap, nil, nil, rooms, rg); ok {
		t.Fatal("clear ground has no step")
	}
}

// A standing, enclosed room whose ground differs from the plan is reconciled:
// the bed on its planned wall cell is packed, and the plan's new door opens the
// roof before it takes a wall out, so the enclosure is never opened early.
func TestPlannedGroundStepReconcilesAStandingRoom(t *testing.T) {
	plan, rooms := groundFixture()
	rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(plan.Rooms[0].Interior)}}
	rg := RetiredGroundOf(plan)
	// The plan wants a second door where a wall stands, and a bed sits on a
	// ring cell.
	door := domain.Cell{X: 9, Z: 11}
	plan.Rooms[0].Doors = []Door{{Cell: door, Rot: domain.West}}
	g := ringWalls(plan, plan.Rooms[0])
	g.walls[door], g.doors[door] = true, false
	bedCell := domain.Cell{X: 13, Z: 12}
	g.walls[bedCell] = false
	bed := playerRow("bed", "Bed", "other", bedCell, bedCell, false)
	bed.Packable = true
	step, ok := PlannedGroundStep(plan, g, []ClearanceTarget{bed}, nil, rooms, rg)
	if !ok || step.Phase != GroundPack || len(step.Targets) != 1 || step.Targets[0].EntityID != "bed" {
		t.Fatalf("the bed on a ring cell is packed first: %+v", step)
	}
	step, ok = PlannedGroundStep(plan, g, nil, nil, rooms, rg)
	if !ok || step.Phase != GroundWalls || len(step.Targets) != 0 || len(step.Roof) != 9 {
		t.Fatalf("roof off before the ring loses a wall: %+v", step)
	}
	rooms.Rooms = nil
	wall := playerRow("wall", "Wall", "ancient_wall_door", door, door, true)
	step, ok = PlannedGroundStep(plan, g, []ClearanceTarget{wall}, nil, rooms, rg)
	if !ok || step.Phase != GroundWalls || len(step.Targets) != 1 || step.Targets[0].EntityID != "wall" {
		t.Fatalf("then one ring wall: %+v", step)
	}
}

// Several rooms and several cells progress in one pass: every idle packable
// piece of every room is one batch, in-use pieces after, and every floor of
// every room is one batch.
func TestPlannedGroundStepBatchesRoomsAndCells(t *testing.T) {
	plan, rooms := groundFixture()
	plan.Rooms = append(plan.Rooms, PlannedRoom{Role: PlannedBedroom, Interior: Rectangle{X: 20, Z: 10, Width: 3, Height: 3}, Door: domain.Cell{X: 21, Z: 9}, DoorRot: domain.South})
	g := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}}
	rg := RetiredGroundOf(plan)
	piece := func(id string, x, z int32, inUse bool) ClearanceTarget {
		row := playerRow(id, "Bed", "other", domain.Cell{X: x, Z: z}, domain.Cell{X: x, Z: z}, false)
		row.Packable, row.InUse = true, inUse
		return row
	}
	rows := []ClearanceTarget{piece("a", 10, 10, true), piece("b", 11, 10, false), piece("c", 20, 10, false), piece("d", 21, 11, false)}
	step, ok := PlannedGroundStep(plan, g, rows, nil, rooms, rg)
	if !ok || step.Phase != GroundPack || len(step.Targets) != 3 {
		t.Fatalf("both rooms' idle pieces in one batch: %+v", step)
	}
	step, ok = PlannedGroundStep(plan, g, rows[:1], nil, rooms, rg)
	if !ok || step.Phase != GroundPack || len(step.Targets) != 1 || step.Targets[0].EntityID != "a" {
		t.Fatalf("the in-use piece after: %+v", step)
	}
	var floors []ClearanceFloor
	for _, r := range plan.Rooms {
		for _, c := range rectCells(r.Interior) {
			floors = append(floors, ClearanceFloor{Cell: c, DefName: "WoodPlankFloor"})
		}
	}
	step, ok = PlannedGroundStep(plan, g, nil, floors, rooms, rg)
	if !ok || step.Phase != GroundFloors || len(step.Floors) != 18 {
		t.Fatalf("every floor of both rooms in one batch: %+v", step)
	}
}

// Packable furniture is packed in one batch, in-use pieces last; a conduit and
// a wall stay deconstructions; a Deconstruct-designated packable piece is
// adopted as a deconstruction, never given an uninstall (#2103).
func TestPlannedGroundStepPacksPackableFurniture(t *testing.T) {
	plan, rooms := groundFixture()
	g, rg := gapGround(plan), RetiredGroundOf(plan)
	piece := func(id, def string, x, z int32, inUse bool) ClearanceTarget {
		row := playerRow(id, def, "other", domain.Cell{X: x, Z: z}, domain.Cell{X: x, Z: z}, false)
		row.Packable, row.InUse = true, inUse
		return row
	}
	bed := piece("a-bed", "Bed", 10, 10, true)
	table := piece("b-table", "Table2x2c", 11, 10, false)
	lamp := piece("c-lamp", "StandingLamp", 12, 10, false)
	step, ok := PlannedGroundStep(plan, g, []ClearanceTarget{bed, table, lamp}, nil, rooms, rg)
	if !ok || step.Phase != GroundPack || len(step.Targets) != 2 || step.Targets[0].EntityID != "b-table" || step.Targets[1].EntityID != "c-lamp" {
		t.Fatalf("one batch, the in-use bed after: %+v", step)
	}
	step, ok = PlannedGroundStep(plan, g, []ClearanceTarget{bed}, nil, rooms, rg)
	if !ok || step.Phase != GroundPack || len(step.Targets) != 1 || step.Targets[0].EntityID != "a-bed" {
		t.Fatalf("the in-use bed last: %+v", step)
	}
	conduit := playerRow("conduit", "PowerConduit", "other", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, false)
	step, ok = PlannedGroundStep(plan, g, []ClearanceTarget{bed, table, conduit}, nil, rooms, rg)
	if !ok || step.Phase != GroundFurniture || len(step.Targets) != 1 || step.Targets[0].EntityID != "conduit" {
		t.Fatalf("a conduit is deconstructed before the packing: %+v", step)
	}
	wall := playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: 11, Z: 11}, domain.Cell{X: 11, Z: 11}, true)
	wall.Packable = true
	if step, ok = PlannedGroundStep(plan, g, []ClearanceTarget{wall}, nil, rooms, rg); !ok || step.Phase != GroundWalls {
		t.Fatalf("a wall is never packed: %+v", step)
	}
	table.Designated = true
	step, ok = PlannedGroundStep(plan, g, []ClearanceTarget{table}, nil, rooms, rg)
	if !ok || step.Phase != GroundFurniture || step.Targets[0].EntityID != "b-table" {
		t.Fatalf("a Deconstruct-designated piece is not packed: %+v", step)
	}
}

// A shelter sleeping spot on planned ground is the colony's only bed until
// the room stands: ground clearance leaves it and its floor.
func TestPlannedGroundStepLeavesTheStandInBed(t *testing.T) {
	plan, rooms := groundFixture()
	g, rg := gapGround(plan), RetiredGroundOf(plan)
	spot := playerRow("spot", "SleepingSpot", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	floors := []ClearanceFloor{{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}}
	if step, ok := PlannedGroundStep(plan, g, []ClearanceTarget{spot}, floors, rooms, rg); ok {
		t.Fatalf("cleared the stand-in bed or its floor: %+v", step)
	}
	if work := PlannedGroundWork(plan, g, []ClearanceTarget{spot}, floors, rooms, rg); len(work) != 0 {
		t.Fatal(work)
	}
}
