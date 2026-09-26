package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestInteriorPlanWalkable(t *testing.T) {
	room := InteriorRoom{Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 4, Height: 4}, Doors: []domain.Cell{{X: 1, Z: -1}, {X: 4, Z: 2}}}
	piece := func(r Rectangle) InteriorPiece {
		return InteriorPiece{Slot: "p", Def: "X", Size: domain.Cell{X: r.Width, Z: r.Height}, Rot: domain.North, Rect: r}
	}
	bench := piece(Rectangle{X: 0, Z: 3, Width: 2, Height: 1})
	bench.InteractionOffset = &domain.Cell{X: 0, Z: -1}
	blocker := piece(Rectangle{X: 0, Z: 2, Width: 1, Height: 1})
	for name, c := range map[string]struct {
		pieces []InteriorPiece
		ok     bool
	}{
		"clear":              {[]InteriorPiece{piece(Rectangle{X: 0, Z: 3, Width: 2, Height: 1})}, true},
		"cuts the aisle":     {[]InteriorPiece{piece(Rectangle{X: 0, Z: 1, Width: 4, Height: 1})}, false},
		"blocks a door":      {[]InteriorPiece{piece(Rectangle{X: 3, Z: 2, Width: 1, Height: 1})}, false},
		"strands a corner":   {[]InteriorPiece{piece(Rectangle{X: 1, Z: 3, Width: 1, Height: 1}), piece(Rectangle{X: 0, Z: 2, Width: 1, Height: 1})}, false},
		"interaction free":   {[]InteriorPiece{bench}, true},
		"interaction buried": {[]InteriorPiece{bench, blocker}, false},
	} {
		err := InteriorPlanWalkable(InteriorPlan{Room: room, Pieces: c.pieces})
		if (err == nil) != c.ok {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestPlanInteriorRefusesAPlanThatBlocksADoor(t *testing.T) {
	corridor := InteriorRoom{Role: RoomRoleBedroom, Interior: Rectangle{X: 0, Z: 0, Width: 1, Height: 3}, Doors: []domain.Cell{{X: 0, Z: -1}}}
	if _, ok := PlanInterior(corridor, InteriorPieceDef{}); !ok {
		t.Fatal("a one-door 1x3 room does not take a bed")
	}
	corridor.Doors = append(corridor.Doors, domain.Cell{X: 0, Z: 3})
	if plan, ok := PlanInterior(corridor, InteriorPieceDef{}); ok {
		t.Fatalf("a bed blocks the second door: %+v", plan.Pieces)
	}
}

func TestInteriorPlacementWalkable(t *testing.T) {
	room := InteriorRoom{Interior: Rectangle{X: 0, Z: 0, Width: 3, Height: 3}, Doors: []domain.Cell{{X: 1, Z: -1}}}
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
