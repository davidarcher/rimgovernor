package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestNextJailStepReconcilesAndMarks(t *testing.T) {
	jail := hallRoom(PlannedPrison, 10, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{jail}}
	if s := NextJailStep(plan, RoomObservation{Shapes: testShapes}, 0, nil, nil); s.Kind != JailNone {
		t.Fatalf("no prisoner: %+v", s)
	}
	if s := NextJailStep(plan, RoomObservation{Shapes: testShapes}, 1, nil, nil); s.Kind != JailReconcile || s.Room.Region.Bounds() != jail.Region.Bounds() || len(s.Template) != 1 {
		t.Fatalf("unbuilt jail: %+v", s)
	}
	centre := domain.Cell{X: jail.Region.Bounds().X + 2, Z: jail.Region.Bounds().Z + 2}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "j", Cells: []domain.Cell{centre}, Enclosed: domain.Known(true)}}}
	plain := SleepingBed{ID: "bed1", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Room: domain.Known("j")}
	if s := NextJailStep(plan, rooms, 1, []SleepingBed{plain}, nil); s.Kind != JailMark || len(s.Marks) != 1 || s.Marks[0] != "bed1" {
		t.Fatalf("plain bed in the jail: %+v", s)
	}
	s := NextJailStep(plan, rooms, 1, nil, nil)
	if s.Kind != JailReconcile || len(s.Template) != 1 || s.Template[0].DefName != testPrimaryBed {
		t.Fatalf("empty jail: %+v", s)
	}
	marked := plain
	marked.Prisoners = domain.Known(true)
	if s := NextJailStep(plan, rooms, 1, []SleepingBed{marked}, nil); s.Kind != JailNone {
		t.Fatalf("one prisoner bed for one prisoner: %+v", s)
	}
	var built []CurrentBuilding
	for _, p := range mustJailPieces(t, jail) {
		built = append(built, CurrentBuilding{ID: p.Slot, Cells: rectCells(p.Rect)})
	}
	if s := NextJailStep(plan, rooms, 9, []SleepingBed{marked}, built); s.Kind != JailNone {
		t.Fatalf("a full jail with no other planned: %+v", s)
	}
}

func mustJailPieces(t *testing.T, r PlannedRoom) []InteriorPiece {
	in, ok := InteriorRoomFromLayout(r, testShapes)
	if !ok {
		t.Fatal("jail room")
	}
	plan, ok := PlanInterior(in, testShapes.Defs[testPrimaryBed])
	if !ok || len(plan.Pieces) == 0 {
		t.Fatal("jail template placed no bed")
	}
	return plan.Pieces
}

func TestNextJailStepMarksTheWholeRoomAndReportsStrays(t *testing.T) {
	jail := hallRoom(PlannedPrison, 10, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []PlannedRoom{jail}}
	centre := domain.Cell{X: jail.Region.Bounds().X + 2, Z: jail.Region.Bounds().Z + 2}
	bed := func(id string, medical, crib, prisoners bool) SleepingBed {
		return SleepingBed{ID: id, Crib: crib, Humanlike: domain.Known(true), Medical: domain.Known(medical), Prisoners: domain.Known(prisoners), Room: domain.Known("j")}
	}
	slave := bed("s", false, false, false)
	slave.Slaves = true
	beds := []SleepingBed{bed("c", false, false, false), bed("a", false, false, false), bed("b", false, false, false), bed("m", true, false, false), bed("k", false, true, false), bed("p", false, false, true), slave}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "j", Cells: []domain.Cell{centre}, Enclosed: domain.Known(true)}}}
	// One prisoner is held and one bed already marked: the rest still flip.
	s := NextJailStep(plan, rooms, 1, beds, nil)
	if s.Kind != JailMark || len(s.Marks) != 4 || s.Marks[0] != "a" || s.Marks[1] != "b" || s.Marks[2] != "c" || s.Marks[3] != "m" {
		t.Fatalf("marks: %+v", s)
	}
	if len(s.Strays) != 2 || s.Strays[0] != "k" || s.Strays[1] != "s" {
		t.Fatalf("strays: %+v", s.Strays)
	}
	// Only the stray remains: nothing to mark, the stray is still reported.
	if s := NextJailStep(plan, rooms, 1, []SleepingBed{bed("k", false, true, false), bed("p", false, false, true)}, nil); s.Kind != JailNone || len(s.Strays) != 1 {
		t.Fatalf("stray only: %+v", s)
	}
	// A map-edge room is not enclosed in the census: no mark, no stray.
	edge := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "j", Cells: []domain.Cell{centre}, Enclosed: domain.Known(false)}}}
	if s := NextJailStep(plan, edge, 1, beds, nil); s.Kind == JailMark || len(s.Marks) != 0 || len(s.Strays) != 0 {
		t.Fatalf("map-edge room: %+v", s)
	}
}
