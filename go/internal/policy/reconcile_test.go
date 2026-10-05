package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// reconFixture is groundFixture's 3x3 room at (10,10) with a ring standing as
// planned, no floors and no furniture.
func reconFixture() (ReconcileInput, PlannedRoom) {
	plan, rooms := groundFixture()
	room := plan.Rooms[0]
	return ReconcileInput{
		Plan: plan, Room: room, Ground: ringWalls(plan, room), Rooms: rooms,
		WantedFloor: func(domain.Cell) string { return "" },
	}, room
}

func readyOp(t *testing.T, rec Reconciliation, kind OpKind) Operation {
	t.Helper()
	for _, op := range rec.Ready {
		if op.Kind == kind {
			return op
		}
	}
	return Operation{}
}

func readyKinds(rec Reconciliation) []OpKind {
	var out []OpKind
	for _, op := range rec.Ready {
		out = append(out, op.Kind)
	}
	return out
}

func kindsEqual(a []OpKind, b ...OpKind) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestReconcileEmptyGroundWavesRingFloorFurniture(t *testing.T) {
	in, _ := reconFixture()
	in.Ground = GroundCensus{}
	in.WantedFloor = func(domain.Cell) string { return "WoodPlankFloor" }
	bed := WantedPiece{DefName: "Bed", Minimum: domain.Cell{X: 10, Z: 10}, Maximum: domain.Cell{X: 10, Z: 11}}
	in.Furniture = []WantedPiece{bed}
	// Wave 1: the ring (16 cells: 15 walls and the door), nothing else.
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn, OpDoorIn) {
		t.Fatalf("wave 1 = %v", k)
	}
	if n := len(readyOp(t, rec, OpWallIn).Cells); n != 15 {
		t.Fatalf("%d ring walls", n)
	}
	if len(rec.Owed) != 4 {
		t.Fatalf("owed = %v", rec.Owed)
	}
	// Wave 2: the ring stands; every floor cell in one batch.
	in.Ground = ringWalls(in.Plan, in.Room)
	rec = Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpFloorIn) || len(rec.Ready[0].Floors) != 9 {
		t.Fatalf("wave 2 = %+v", rec.Ready)
	}
	// Wave 3: floors laid; the bed builds.
	for _, c := range rectCells(in.Room.Interior) {
		in.Floors = append(in.Floors, ClearanceFloor{Cell: c, DefName: "WoodPlankFloor"})
	}
	rec = Reconcile(in)
	if op := readyOp(t, rec, OpBuild); !kindsEqual(readyKinds(rec), OpBuild) || len(op.Pieces) != 1 || op.Pieces[0] != bed {
		t.Fatalf("wave 3 = %+v", rec.Ready)
	}
	// Stocked packed bed: install instead.
	in.Stock = map[string]int{"Bed": 1}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpInstall) {
		t.Fatalf("stock first: %v", k)
	}
	// Done: the room owes nothing.
	in.Rows = []ClearanceTarget{playerRow("bed", "Bed", "other", bed.Minimum, bed.Maximum, false)}
	if rec = Reconcile(in); len(rec.Owed) != 0 || len(rec.Ready) != 0 {
		t.Fatalf("complete room owes %+v", rec.Owed)
	}
}

func TestReconcileWrongFloorOutBeforeIn(t *testing.T) {
	in, _ := reconFixture()
	in.WantedFloor = func(domain.Cell) string { return "StoneTile" }
	for _, c := range rectCells(in.Room.Interior) {
		in.Floors = append(in.Floors, ClearanceFloor{Cell: c, DefName: "WoodPlankFloor"})
	}
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpFloorOut) || len(rec.Ready[0].Cells) != 9 {
		t.Fatalf("ready = %+v", rec.Ready)
	}
	if len(rec.Owed) != 2 {
		t.Fatalf("floor in is owed behind it: %+v", rec.Owed)
	}
	// Once the old floor is gone only floor in remains.
	in.Floors = nil
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpFloorIn) {
		t.Fatalf("after: %v", k)
	}
}

func TestReconcileTwelveFloorsAreOneBatch(t *testing.T) {
	in, room := reconFixture()
	room.Interior = Rectangle{X: 10, Z: 10, Width: 4, Height: 3}
	room.Door = domain.Cell{X: 11, Z: 9}
	in.Plan = LayoutPlan{Rooms: []PlannedRoom{room}}
	in.Room, in.Ground = room, ringWalls(in.Plan, room)
	in.WantedFloor = func(domain.Cell) string { return "StoneTile" }
	rec := Reconcile(in)
	if len(rec.Ready) != 1 || rec.Ready[0].Kind != OpFloorIn || len(rec.Ready[0].Floors) != 12 {
		t.Fatalf("ready = %+v", rec.Ready)
	}
}

func TestReconcileOffSlotFurniturePacksThenInstalls(t *testing.T) {
	in, _ := reconFixture()
	in.Furniture = []WantedPiece{{DefName: "Bed", Minimum: domain.Cell{X: 12, Z: 10}, Maximum: domain.Cell{X: 12, Z: 11}}}
	bed := playerRow("bed", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	bed.Packable = true
	in.Rows = []ClearanceTarget{bed}
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpPack) || rec.Ready[0].Targets[0].EntityID != "bed" {
		t.Fatalf("pack first: %+v", rec.Ready)
	}
	if len(rec.Owed) != 2 || rec.Owed[1].Kind != OpInstall {
		t.Fatalf("install owed behind the pack: %+v", rec.Owed)
	}
	// Packed: the stock holds the piece.
	in.Rows, in.Stock = nil, map[string]int{"Bed": 1}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpInstall) {
		t.Fatalf("install after: %v", k)
	}
	// A piece that cannot pack is deconstructed and rebuilt.
	chair := playerRow("chair", "Armchair", "other", domain.Cell{X: 10, Z: 12}, domain.Cell{X: 10, Z: 12}, false)
	in.Rows, in.Stock, in.Furniture = []ClearanceTarget{chair}, nil, nil
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpFurnitureOut) {
		t.Fatalf("deconstruct: %v", k)
	}
}

func TestReconcileInUsePieceLast(t *testing.T) {
	in, _ := reconFixture()
	used := playerRow("used", "Bed", "other", domain.Cell{X: 10, Z: 10}, domain.Cell{X: 10, Z: 11}, false)
	used.Packable, used.InUse = true, true
	idle := playerRow("idle", "Bed", "other", domain.Cell{X: 12, Z: 10}, domain.Cell{X: 12, Z: 11}, false)
	idle.Packable = true
	in.Rows = []ClearanceTarget{used, idle}
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpPack) || rec.Ready[0].Targets[0].EntityID != "idle" {
		t.Fatalf("idle first: %+v", rec.Ready)
	}
	in.Rows = []ClearanceTarget{used}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpPackInUse) {
		t.Fatalf("in-use after: %v", k)
	}
}

func TestReconcileStandingRoomKeepsItsRing(t *testing.T) {
	in, room := reconFixture()
	in.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(room.Interior)}}
	// A door the plan lacks is swapped for a wall in place, roof untouched.
	swap := domain.Cell{X: 9, Z: 10}
	in.Ground.walls[swap], in.Ground.doors[swap] = false, true
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpDoorOut) || rec.Ready[0].Cells[0] != swap {
		t.Fatalf("swap in place: %+v", rec.Ready)
	}
	in.Ground.doors[swap], in.Ground.walls[swap] = false, true
	// The plan now wants two more doors on walls: the roof comes off first,
	// then one ring wall at a time.
	a, b := domain.Cell{X: 9, Z: 11}, domain.Cell{X: 9, Z: 12}
	in.Room.Doors = []Door{{Cell: a, Rot: domain.West}, {Cell: b, Rot: domain.West}}
	in.Plan = LayoutPlan{Rooms: []PlannedRoom{in.Room}}
	rec = Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpRoofOff) || len(rec.Ready[0].Cells) != 9 {
		t.Fatalf("roof off first: %+v", rec.Ready)
	}
	var out Operation
	for _, op := range rec.Owed {
		if op.Kind == OpWallOut {
			out = op
		}
	}
	if len(out.Cells) != 2 {
		t.Fatalf("both ring walls are owed: %+v", rec.Owed)
	}
	in.Rooms.Rooms = nil // the roof is off, the room no longer enclosed
	rec = Reconcile(in)
	if op := readyOp(t, rec, OpWallOut); len(op.Cells) != 1 {
		t.Fatalf("one ring wall per pass: %+v", rec.Ready)
	}
	if op := readyOp(t, rec, OpDoorIn); len(op.Cells) != 0 {
		t.Fatalf("a door waits for its wall to come out: %+v", op)
	}
}
