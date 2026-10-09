package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Door-to-door walkability. Pawns walk a room's free floor; a piece
// may not cut the aisle between its doors, wall off any floor, or stand on
// another piece's interaction cell. Reachability is over orthogonal steps,
// the conservative reading of RimWorld's no-corner-cutting pathing.

// InteriorPlanWalkable checks a plan's world pieces against its room: every
// door threshold is floor and all thresholds join, every free cell is
// reachable from them, and every interaction cell inside the room is free.
func InteriorPlanWalkable(plan InteriorPlan) error {
	room := plan.Room
	blocked := map[domain.Cell]bool{}
	for _, p := range plan.Pieces {
		for _, c := range rectCells(p.Rect) {
			blocked[c] = true
		}
	}
	thresholds := interiorThresholds(room)
	for _, t := range thresholds {
		if blocked[t] {
			return fmt.Errorf("a piece stands inside the door at %v", t)
		}
	}
	reach := interiorReach(room.Interior, blocked, thresholds)
	for _, c := range rectCells(room.Interior) {
		if !blocked[c] && !reach[c] {
			return fmt.Errorf("floor at %v is cut off from the doors", c)
		}
	}
	for _, p := range plan.Pieces {
		if c, ok := p.Interaction(); ok && rectContains(room.Interior, c) && blocked[c] {
			return fmt.Errorf("%s: interaction cell %v is blocked", p.Slot, c)
		}
	}
	return nil
}

// InteriorPlacementWalkable reports whether adding a footprint to a room
// whose blocked cells are given keeps every door threshold and every free
// cell that was reachable from the doors reachable. A room already cut up
// by earlier furniture is judged only on what the new piece would take.
func InteriorPlacementWalkable(room InteriorRoom, blocked map[domain.Cell]bool, footprint []domain.Cell) bool {
	thresholds := interiorThresholds(room)
	before := interiorReach(room.Interior, blocked, thresholds)
	after := map[domain.Cell]bool{}
	for c := range blocked {
		after[c] = true
	}
	placed := map[domain.Cell]bool{}
	for _, c := range footprint {
		after[c], placed[c] = true, true
	}
	for _, t := range thresholds {
		if !blocked[t] && placed[t] {
			return false
		}
	}
	reach := interiorReach(room.Interior, after, thresholds)
	for c := range before {
		if !placed[c] && !reach[c] {
			return false
		}
	}
	return true
}

// interiorThresholds are the floor cells just inside the room's doors.
func interiorThresholds(room InteriorRoom) []domain.Cell {
	r := room.Interior
	var cells []domain.Cell
	for _, d := range room.Doors {
		side, ok := doorSide(r, d)
		if !ok {
			continue
		}
		o := rotateOffset(domain.Cell{X: 0, Z: 1}, side)
		cells = append(cells, domain.Cell{X: d.X - o.X, Z: d.Z - o.Z})
	}
	return cells
}

// interiorReach floods the room's unblocked floor from the free
// thresholds; the result holds a threshold only when every threshold
// joins the first free one.
func interiorReach(r Rectangle, blocked map[domain.Cell]bool, thresholds []domain.Cell) map[domain.Cell]bool {
	reach := map[domain.Cell]bool{}
	var queue []domain.Cell
	for _, t := range thresholds {
		if !blocked[t] && rectContains(r, t) {
			queue = append(queue, t)
			break
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		if reach[c] || blocked[c] || !rectContains(r, c) {
			continue
		}
		reach[c] = true
		queue = append(queue, domain.Cell{X: c.X + 1, Z: c.Z}, domain.Cell{X: c.X - 1, Z: c.Z}, domain.Cell{X: c.X, Z: c.Z + 1}, domain.Cell{X: c.X, Z: c.Z - 1})
	}
	for _, t := range thresholds {
		if !blocked[t] && !reach[t] {
			return map[domain.Cell]bool{}
		}
	}
	return reach
}

func rectContains(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}
