package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// reshaped is the 3x3 bedroom of groundFixture with its ring standing, the plan
// now wanting region instead: the census still holds the old ring.
func reshaped(region Region) (ReconcileInput, GroundCensus) {
	in, _ := reconFixture()
	old := ringWalls(in.Plan, in.Room)
	in.Room.Region = region
	in.Plan = LayoutPlan{Rooms: []PlannedRoom{in.Room}}
	in.Ground = old
	return in, old
}

func wallRows(cells ...domain.Cell) []ClearanceTarget {
	var rows []ClearanceTarget
	for _, c := range cells {
		rows = append(rows, playerRow("w"+GroundFloorID(c), ShellWallDefinition, "ancient_wall_door", c, c, true))
	}
	return rows
}

func owedKinds(rec Reconciliation) []OpKind {
	var out []OpKind
	for _, op := range rec.Owed {
		out = append(out, op.Kind)
	}
	return out
}

// Enlarging by a column: the new east wall goes up first; the old east wall
// (now inside) opens only when it stands, roof off ahead of it.
func TestReconcileEnlargeBuildsNewRingBeforeOpeningOld(t *testing.T) {
	in, old := reshaped(RectRegion(Rectangle{X: 10, Z: 10, Width: 4, Height: 3}))
	oldEast := []domain.Cell{{X: 13, Z: 10}, {X: 13, Z: 11}, {X: 13, Z: 12}}
	in.Rows = wallRows(oldEast...)
	oldRoom := rectCells(Rectangle{X: 10, Z: 10, Width: 3, Height: 3})
	in.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: oldRoom}}

	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn) || len(rec.Ready[0].Cells) != 5 {
		t.Fatalf("new ring first: %+v", rec.Ready)
	}
	if k := owedKinds(rec); !kindsEqual(k, OpWallOut, OpWallIn) {
		t.Fatalf("old wall owed but not ready, no roof off yet: %v", k)
	}

	// The new east wall stands: the roof of the old room comes off, then its east wall.
	for z := int32(9); z <= 13; z++ {
		old.walls[domain.Cell{X: 14, Z: z}] = true
	}
	in.Ground = old
	rec = Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpRoofOff) || len(rec.Ready[0].Cells) != 9 {
		t.Fatalf("roof off first: %+v", rec.Ready)
	}
	in.Rooms.Rooms = nil
	rec = Reconcile(in)
	if op := readyOp(t, rec, OpWallOut); len(op.Cells) != 3 {
		t.Fatalf("old east wall opens: %+v", rec.Ready)
	}
}

// Shrinking by a row: the new wall is raised inside the old room; the old
// bottom wall is retired ground and holds until the new one stands.
func TestReconcileShrinkRetiredWallWaitsForNewRing(t *testing.T) {
	in, old := reshaped(RectRegion(Rectangle{X: 10, Z: 10, Width: 3, Height: 2}))
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn) || len(rec.Ready[0].Cells) != 3 {
		t.Fatalf("new row of wall: %+v", rec.Ready)
	}
	oldBottom := []domain.Cell{{X: 10, Z: 13}, {X: 11, Z: 13}, {X: 12, Z: 13}}
	rows := wallRows(oldBottom...)
	retired := RectRegion(Rectangle{X: 10, Z: 13, Width: 3, Height: 1})
	in.Plan.RetiredGround = []Region{retired}
	rg := RetiredGroundOf(in.Plan)
	// The old room still stands whole behind its bottom wall.
	in.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(Rectangle{X: 10, Z: 10, Width: 3, Height: 3})}}
	if step, ok := PlannedGroundStep(in.Plan, old, rows, nil, in.Rooms, rg, nil); ok {
		t.Fatalf("old wall opened with the new ring owed: %+v", step)
	}
	for x := int32(10); x <= 12; x++ {
		old.walls[domain.Cell{X: x, Z: 12}] = true
	}
	// The new wall splits the old room off: the new room stands, the bottom wall bounds nothing.
	in.Rooms.Rooms = []Room{{ID: "r", Enclosed: domain.Known(true), Cells: rectCells(Rectangle{X: 10, Z: 10, Width: 3, Height: 2})}}
	step, ok := PlannedGroundStep(in.Plan, old, rows, nil, in.Rooms, rg, nil)
	if !ok || step.Phase != GroundWalls || len(step.Targets) != 3 || len(step.Roof) != 0 {
		t.Fatalf("old wall comes down once the new ring stands: %+v ok=%v", step, ok)
	}
}

// Rect to L: only the notch's new wall is owed; walls beyond the notch are no
// part of the room's ground.
func TestReconcileRectToLRaisesNotchWall(t *testing.T) {
	var cells []domain.Cell
	for _, c := range rectCells(Rectangle{X: 10, Z: 10, Width: 3, Height: 3}) {
		if c != (domain.Cell{X: 12, Z: 12}) {
			cells = append(cells, c)
		}
	}
	in, _ := reshaped(RegionOf(cells))
	in.Rows = wallRows(domain.Cell{X: 13, Z: 13})
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn) {
		t.Fatalf("ready = %+v", rec.Ready)
	}
	if got := rec.Ready[0].Cells; len(got) != 1 || got[0] != (domain.Cell{X: 12, Z: 12}) {
		t.Fatalf("notch wall = %v", got)
	}
	if k := owedKinds(rec); !kindsEqual(k, OpWallIn) {
		t.Fatalf("nothing else owed: %v", k)
	}
}
