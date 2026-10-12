package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPlanRoomInteriorRefusesAPlanThatBlocksADoor(t *testing.T) {
	corridor := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 1, Height: 3}, Doors: []domain.Cell{{X: 0, Z: -1}}}
	if _, ok := PlanRoomInterior(corridor, InteriorPieceDef{}); !ok {
		t.Fatal("a one-door 1x3 room does not take a bed")
	}
	corridor.Doors = append(corridor.Doors, domain.Cell{X: 0, Z: 3})
	if plan, ok := PlanRoomInterior(corridor, InteriorPieceDef{}); ok {
		t.Fatalf("a bed blocks the second door: %+v", plan.Pieces)
	}
}

func TestInteriorPlacementWalkable(t *testing.T) {
	room := InteriorRoom{Shapes: testShapes, Interior: Rectangle{X: 0, Z: 0, Width: 3, Height: 3}, Doors: []domain.Cell{{X: 1, Z: -1}}}
	for name, c := range map[string]struct {
		footprint []domain.Cell
		ok        bool
	}{
		"corner":        {[]domain.Cell{{X: 0, Z: 2}}, true},
		"threshold":     {[]domain.Cell{{X: 1, Z: 0}}, false},
		"wall to wall":  {[]domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 1}, {X: 2, Z: 1}}, false},
		"beside a door": {[]domain.Cell{{X: 0, Z: 0}}, true},
	} {
		if got := InteriorPlacementWalkable(room, map[domain.Cell]bool{}, c.footprint); got != c.ok {
			t.Errorf("%s: %v", name, got)
		}
	}
	// A room already cut up is judged on what the new piece takes.
	cut := map[domain.Cell]bool{{X: 0, Z: 1}: true, {X: 1, Z: 1}: true, {X: 2, Z: 1}: true}
	if !InteriorPlacementWalkable(room, cut, []domain.Cell{{X: 0, Z: 2}}) {
		t.Error("a stranded cell already lost refuses the piece")
	}
	if InteriorPlacementWalkable(room, map[domain.Cell]bool{{X: 0, Z: 2}: true, {X: 2, Z: 1}: true}, []domain.Cell{{X: 1, Z: 2}}) {
		t.Error("a piece that strands the last corner is accepted")
	}
}
