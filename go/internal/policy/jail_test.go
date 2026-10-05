package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestNextJailStepShellsMarksThenPlaces(t *testing.T) {
	jail := hallRoom(ModulePrison, 10, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []LayoutRoom{jail}}
	if s := NextJailStep(plan, RoomObservation{Shapes: testShapes}, 0, nil, nil); s.Kind != JailNone {
		t.Fatalf("no prisoner: %+v", s)
	}
	if s := NextJailStep(plan, RoomObservation{Shapes: testShapes}, 1, nil, nil); s.Kind != JailShell || s.Room.Interior != jail.Interior {
		t.Fatalf("unbuilt jail: %+v", s)
	}
	centre := domain.Cell{X: jail.Interior.X + 2, Z: jail.Interior.Z + 2}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "j", Cells: []domain.Cell{centre}, Enclosed: domain.Known(true)}}}
	plain := SleepingBed{ID: "bed1", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Room: domain.Known("j")}
	if s := NextJailStep(plan, rooms, 1, []SleepingBed{plain}, nil); s.Kind != JailMark || s.Bed != "bed1" {
		t.Fatalf("plain bed in the jail: %+v", s)
	}
	s := NextJailStep(plan, rooms, 1, nil, nil)
	if s.Kind != JailPlace || s.Piece.Def != testPrimaryBed {
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

func mustJailPieces(t *testing.T, r LayoutRoom) []InteriorPiece {
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
