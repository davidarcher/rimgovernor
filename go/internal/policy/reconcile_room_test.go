package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wantsAll wants a floor no existing floor equals and none stands in for, so
// every constructed floor on reconciled ground is owed its removal.
func wantsAll(PlannedRoom) (func(domain.Cell) string, func(have, want string) bool) {
	return func(domain.Cell) string { return "(wanted)" }, nil
}

// wantsFloor wants def in every cell of a room and lets the floors named keeps
// stand in for it.
func wantsFloor(def string, keeps ...string) RoomFloors {
	return func(PlannedRoom) (func(domain.Cell) string, func(have, want string) bool) {
		return func(domain.Cell) string { return def }, func(have, _ string) bool { return slices.Contains(keeps, have) }
	}
}

func TestPlannedGroundStepKeepsTheWantedAndAdequateFloors(t *testing.T) {
	plan, rooms := groundFixture()
	g := gapGround(plan)
	rg := RetiredGroundOf(plan)
	floors := []ClearanceFloor{{Cell: domain.Cell{X: 10, Z: 10}, DefName: "WoodPlankFloor"}, {Cell: domain.Cell{X: 11, Z: 10}, DefName: "TileGranite"}, {Cell: domain.Cell{X: 12, Z: 10}, DefName: "Concrete"}}
	// The wanted floor and the one that stands in for it stay; only the
	// inadequate one is torn up.
	step, ok := PlannedGroundStep(plan, g, nil, floors, rooms, rg, wantsFloor("WoodPlankFloor", "TileGranite"))
	if !ok || step.Phase != GroundFloors || len(step.Floors) != 1 || step.Floors[0].DefName != "Concrete" {
		t.Fatal(step, ok)
	}
	// With no flooring decision the floors are left alone.
	if step, ok := PlannedGroundStep(plan, g, nil, floors, rooms, rg, nil); ok {
		t.Fatal(step)
	}
}

func TestReconcileKeepsAnAdequateFloorUnlessTheTitleTagsIt(t *testing.T) {
	in, room := reconFixture()
	in.WantedFloor = func(domain.Cell) string { return "TileGranite" }
	for _, c := range rectCells(room.Interior) {
		in.Floors = append(in.Floors, ClearanceFloor{Cell: c, DefName: "WoodPlankFloor"})
	}
	// Exact match only: the wood floor is torn up for the tile.
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpFloorOut) {
		t.Fatal(k)
	}
	// The wood floor stands in for it: no tear-up, nothing owed.
	in.FloorKept = func(have, want string) bool { return have == "WoodPlankFloor" }
	if rec := Reconcile(in); len(rec.Owed) != 0 {
		t.Fatalf("an adequate floor is kept: %+v", rec.Owed)
	}
	// A title tagged floor is the exception (FloorKept).
	facts := wantedFloorFacts()
	p := flooringPolicy()
	kept := FloorKept(wantedFloorRoom(PlannedThrone), []string{"FineFloor"}, facts, p)
	if kept("WoodPlankFloor", "TileGranite") || !kept("TileGranite", "TileGranite") {
		t.Fatal("the throne room keeps only a floor carrying the title tag")
	}
	if !FloorKept(wantedFloorRoom(PlannedBedroom), nil, facts, p)("WoodPlankFloor", "TileGranite") {
		t.Fatal("a living room keeps an adequate floor")
	}
	// A floor the mirror does not describe is never torn up on a missing fact.
	if !kept("Mystery", "TileGranite") {
		t.Fatal("unknown floor")
	}
}

// The roof is read from the census room: a room whose roof is off is no
// longer enclosed, so its walls are ready and the roof is not removed again.
func TestReconcileReadsRoofStateFromTheCensusRoom(t *testing.T) {
	in, room := reconFixture()
	a := domain.Cell{X: 9, Z: 11}
	in.Room.Doors = []Door{{Cell: a, Rot: domain.West}}
	in.Plan = LayoutPlan{Rooms: []PlannedRoom{in.Room}}
	in.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(room.Interior)}}
	if k := readyKinds(Reconcile(in)); !kindsEqual(k, OpRoofOff) {
		t.Fatal("an enclosed census room owes its roof off first", k)
	}
	for name, rooms := range map[string][]Room{
		"not enclosed": {{ID: "r", Enclosed: domain.Known(false), Cells: rectCells(room.Interior)}},
		"gone":         nil,
	} {
		in.Rooms.Rooms = rooms
		k := readyKinds(Reconcile(in))
		if slices.Contains(k, OpRoofOff) || !slices.Contains(k, OpWallOut) {
			t.Fatal(name, k)
		}
	}
}

func TestReconcileRoomLeavesTheRingRemovalsToTheClearSide(t *testing.T) {
	in, room := reconFixture()
	swap := domain.Cell{X: 9, Z: 10}
	in.Ground.walls[swap], in.Ground.doors[swap] = false, true
	in.Rows = []ClearanceTarget{playerRow("swap", "Door", "ancient_wall_door", swap, swap, true)}
	in.Furniture = []WantedPiece{{DefName: "Throne", Minimum: domain.Cell{X: 10, Z: 10}, Maximum: domain.Cell{X: 10, Z: 10}}}
	if k := readyKinds(Reconcile(in)); !slices.Contains(k, OpDoorOut) {
		t.Fatal("the diff owes the door swap", k)
	}
	var kinds []OpKind
	for _, op := range ReconcileRoom(in) {
		kinds = append(kinds, op.Kind)
	}
	if !kindsEqual(kinds, OpBuild) {
		t.Fatal(kinds, room)
	}
}

func TestOwnRowsKeepsTheRingTheTemplateAndTheForbidden(t *testing.T) {
	throne := WantedPiece{DefName: "Throne", Minimum: domain.Cell{X: 10, Z: 10}, Maximum: domain.Cell{X: 10, Z: 10}}
	rows := []ClearanceTarget{
		playerRow("wall", "Wall", "ancient_wall_door", domain.Cell{X: 9, Z: 9}, domain.Cell{X: 9, Z: 9}, true),
		playerRow("throne", "Throne", "other", throne.Minimum, throne.Maximum, false),
		playerRow("sculpture", "SculptureSmall", "other", domain.Cell{X: 11, Z: 10}, domain.Cell{X: 11, Z: 10}, false),
		playerRow("butcher", "TableButcher", "other", domain.Cell{X: 12, Z: 10}, domain.Cell{X: 13, Z: 10}, false),
		playerRow("frame", "Frame_Brazier", "other", domain.Cell{X: 10, Z: 12}, domain.Cell{X: 10, Z: 12}, false),
	}
	got := OwnRows(rows, []WantedPiece{throne}, func(def string) bool { return def == "TableButcher" })
	var ids []string
	for _, r := range got {
		ids = append(ids, r.EntityID)
	}
	if !slices.Equal(ids, []string{"wall", "throne", "butcher", "frame"}) {
		t.Fatal(ids)
	}
}
