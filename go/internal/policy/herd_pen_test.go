package policy

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testPenMarker = InteriorPieceDef{Def: PenMarkerDefinition, Size: domain.Cell{X: 1, Z: 1}}

func penBuilding(t *testing.T, def string, c domain.Cell) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(def, c, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: fmt.Sprintf("%s@%d,%d", def, c.X, c.Z), Building: b, Cells: []domain.Cell{c}}
}

// penRing is the fences and gate standing on room's ring, built from the plan.
func penRing(t *testing.T, plan LayoutPlan, room PlannedRoom) []CurrentBuilding {
	t.Helper()
	doors := plan.ShellDoors(room)
	ring := roomWalls(room)
	var out []CurrentBuilding
	for _, c := range rectCells(ring) {
		switch {
		case !onRing(c, ring):
		case slices.Contains(doors, c):
			out = append(out, penBuilding(t, PenGateDefinition, c))
		default:
			out = append(out, penBuilding(t, PenFenceDefinition, c))
		}
	}
	return out
}

func penInput(plan LayoutPlan, step PenStep, built []CurrentBuilding) ReconcileInput {
	// A wanted floor proves a pen is never owed one; the standing marker is the
	// census row its template piece matches.
	in := ReconcileInput{Plan: plan, Room: step.Room, Ground: GroundOf(built), Furniture: step.Template, WantedFloor: func(domain.Cell) string { return "(wanted)" }}
	for _, b := range built {
		if b.Building.Definition() == PenMarkerDefinition {
			in.Rows = append(in.Rows, ClearanceTarget{EntityID: b.ID, DefName: PenMarkerDefinition, Minimum: b.Cells[0], Maximum: b.Cells[0], Player: true})
		}
	}
	return in
}

func TestPenIsAnOutdoorRoomWithAGateOnItsRing(t *testing.T) {
	plan := herdTestPlan(t, 20)
	pens := plan.HerdRooms(PlannedPen)
	if len(pens) == 0 {
		t.Fatal("the plan holds no pen")
	}
	room := pens[0]
	if !room.Outdoor || room.Role != PlannedPen {
		t.Fatal(room)
	}
	if wall, door := room.RingDefs(); wall != PenFenceDefinition || door != PenGateDefinition {
		t.Fatal(wall, door)
	}
	doors := plan.ShellDoors(room)
	ring := roomWalls(room)
	if len(doors) != 1 || !onRing(doors[0], ring) {
		t.Fatal("one gate on the ring", doors)
	}
	for _, corner := range []domain.Cell{{X: ring.X, Z: ring.Z}, {X: ring.X + ring.Width - 1, Z: ring.Z + ring.Height - 1}} {
		if doors[0] == corner {
			t.Fatal("the gate is on a corner")
		}
	}
	// The pen's ring is not an indoor room's: walls and a door standing there
	// do not match.
	indoor := room
	indoor.Outdoor = false
	if plan.GroundMatches(indoor, GroundOf(penRing(t, plan, room))) {
		t.Fatal("fences read as walls")
	}
}

// An empty pen site owes the fence ring and its gate, then the marker, and
// never a roof or a floor.
func TestPenReconcilesFencesGateAndMarkerNeverRoofOrFloor(t *testing.T) {
	plan := herdTestPlan(t, 20)
	step, sited := NextPenStep(plan, GroundOf(nil), nil, testPenMarker)
	if !sited || step.Ring || step.Marker || step.Stands() {
		t.Fatal(step, sited)
	}
	rec := Reconcile(penInput(plan, step, nil))
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn, OpDoorIn, OpBuild) {
		t.Fatal(k)
	}
	if gate := readyOp(t, rec, OpDoorIn); !slices.Equal(gate.Cells, plan.ShellDoors(step.Room)) {
		t.Fatal(gate)
	}
	ring := roomWalls(step.Room)
	if fences := readyOp(t, rec, OpWallIn); len(fences.Cells) != int(2*ring.Width+2*ring.Height-4-1) {
		t.Fatal("every ring cell but the gate is fenced", len(fences.Cells))
	}
	marker := readyOp(t, rec, OpBuild)
	if len(marker.Pieces) != 1 || marker.Pieces[0].DefName != PenMarkerDefinition || !rectInside(step.Room.Interior, Rectangle{X: marker.Pieces[0].Minimum.X, Z: marker.Pieces[0].Minimum.Z, Width: 1, Height: 1}) {
		t.Fatal(marker)
	}
	for _, op := range rec.Owed {
		if op.Kind == OpRoofOff || op.Kind == OpFloorIn || op.Kind == OpFloorOut {
			t.Fatal("a pen is never roofed or floored", op)
		}
	}
}

func TestPenStandsOnceItsRingAndMarkerDo(t *testing.T) {
	plan := herdTestPlan(t, 20)
	step, _ := NextPenStep(plan, GroundOf(nil), nil, testPenMarker)
	in := step.Room.Interior
	built := append(penRing(t, plan, step.Room), penBuilding(t, PenMarkerDefinition, domain.Cell{X: in.X + 1, Z: in.Z + 1}))
	step, sited := NextPenStep(plan, GroundOf(built), built, testPenMarker)
	if !sited || !step.Stands() {
		t.Fatal(step)
	}
	if rec := Reconcile(penInput(plan, step, built)); len(rec.Owed) != 0 {
		t.Fatal("a standing pen owes nothing", rec.Owed)
	}
	// The ring alone leaves the marker owed.
	ringOnly := penRing(t, plan, step.Room)
	step, _ = NextPenStep(plan, GroundOf(ringOnly), ringOnly, testPenMarker)
	if !step.Ring || step.Marker || !kindsEqual(readyKinds(Reconcile(penInput(plan, step, ringOnly))), OpBuild) {
		t.Fatal(step)
	}
}

// A fence lost after the pen stood is rebuilt by the diff on the same site: no
// new site is picked.
func TestPenRebuildsALostFenceInPlace(t *testing.T) {
	plan := herdTestPlan(t, 20)
	step, _ := NextPenStep(plan, GroundOf(nil), nil, testPenMarker)
	in := step.Room.Interior
	built := append(penRing(t, plan, step.Room), penBuilding(t, PenMarkerDefinition, domain.Cell{X: in.X + 1, Z: in.Z + 1}))
	var lost domain.Cell
	for i, b := range built {
		if b.Building.Definition() == PenFenceDefinition {
			lost = b.Cells[0]
			built = slices.Delete(built, i, i+1)
			break
		}
	}
	again, sited := NextPenStep(plan, GroundOf(built), built, testPenMarker)
	if !sited || again.Stands() || again.Ring || !again.Marker || !again.Room.Same(step.Room) {
		t.Fatal(again)
	}
	rec := Reconcile(penInput(plan, again, built))
	if k := readyKinds(rec); !kindsEqual(k, OpWallIn) || !slices.Equal(readyOp(t, rec, OpWallIn).Cells, []domain.Cell{lost}) {
		t.Fatal(k, rec.Ready)
	}
}

func TestNoPenReservationIsNoSite(t *testing.T) {
	plan := herdTestPlan(t, 20)
	var kept []LayoutReservation
	for _, r := range plan.Reservations {
		if r.Kind != ReservePen {
			kept = append(kept, r)
		}
	}
	plan.Reservations = kept
	if _, sited := NextPenStep(plan, GroundOf(nil), nil, testPenMarker); sited {
		t.Fatal("no pen planned")
	}
}
