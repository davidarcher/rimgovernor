package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Infestation bait room (#1069, epic #845). Hives pick dark open ground
// under overhead mountain; once #1067 walls off the pockets and lights
// the base, one dark room far from the base is left for them, stocked
// with cheap flammables and an incendiary IED (or spike traps when insect
// jelly is wanted) so the hive burns or bleeds where no colonist lives.

const (
	// ReserveBaitRoom is the bait room's footprint, its wall ring included.
	ReserveBaitRoom ReservationKind = "bait_room"
	// ReserveBaitWall is one wall-ring cell of the bait room that is not
	// natural rock and so needs a built wall.
	ReserveBaitWall ReservationKind = "bait_wall"
)

const (
	// baitInterior is the side of the bait room's square floor.
	baitInterior int32 = 3
	// baitGap is how far outside the perimeter ring's bounds the bait
	// room's walls must stay.
	baitGap int32 = 20
	// TierBaitPrefix names the bait room's section under the perimeter's
	// prefix, so the perimeter recut builds and removes it with the wall.
	TierBaitPrefix = TierPerimeterPrefix + "bait-"
	// BaitIED and BaitTrap are the vanilla defs the bait room arms with.
	BaitIED  = "TrapIED_Incendiary"
	BaitTrap = "TrapSpike"
)

// PlanBaitRoom replaces plan's bait-room reservations with the footprint
// (wall ring included) of the square room nearest the perimeter ring that
// lies wholly under thick roof, at least baitGap outside the ring's
// bounds, on no built cell and no cell another room, hallway or
// reservation uses, with an open floor and an open door cell at the
// middle of its south wall. Wall cells may be natural rock; each open one
// gets a bait-wall reservation. A plan without a perimeter, or a map
// with no such spot, holds none.
func PlanBaitRoom(plan LayoutPlan, s MapSurvey) LayoutPlan {
	var kept []LayoutReservation
	var ring Rectangle
	used := map[domain.Cell]bool{}
	for _, r := range plan.Reservations {
		if r.Kind == ReserveBaitRoom || r.Kind == ReserveBaitWall {
			continue
		}
		kept = append(kept, r)
		if r.Kind == ReservePerimeter {
			ring = unionRect(ring, r.Area)
		}
		for _, c := range rectCells(r.Area) {
			used[c] = true
		}
	}
	plan.Reservations = kept
	if ring.Width == 0 {
		return plan
	}
	for _, r := range plan.Rooms {
		for _, c := range rectCells(roomWalls(r)) {
			used[c] = true
		}
	}
	for _, sg := range plan.Spine {
		for _, c := range rectCells(pad(rectOf(sg.From, sg.To), SpineWidth/2)) {
			used[c] = true
		}
	}
	cells := make(map[domain.Cell]SurveyCell, len(s.Cells))
	for _, c := range s.Cells {
		cells[c.Cell] = c
	}
	keep := pad(ring, baitGap)
	centre := domain.Cell{X: ring.X + ring.Width/2, Z: ring.Z + ring.Height/2}
	side := baitInterior + 2
	var best Rectangle
	bestD := -1.0
	for _, c := range s.Cells {
		r := Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: side, Height: side}
		if clipRect(r, keep).Width != 0 || !baitFits(r, cells, used) {
			continue
		}
		d := distance(domain.Cell{X: r.X + side/2, Z: r.Z + side/2}, centre)
		if bestD < 0 || d < bestD || d == bestD && cellLess(c.Cell, domain.Cell{X: best.X, Z: best.Z}) {
			best, bestD = r, d
		}
	}
	if bestD < 0 {
		return plan
	}
	plan.Reservations = append(plan.Reservations, LayoutReservation{Kind: ReserveBaitRoom, Area: best})
	in, door := pad(best, -1), baitDoor(best)
	for _, c := range rectCells(best) {
		if !contains(in, c) && c != door && !cells[c].Rock {
			plan.Reservations = append(plan.Reservations, LayoutReservation{Kind: ReserveBaitWall, Area: Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}})
		}
	}
	return plan
}

func baitFits(r Rectangle, cells map[domain.Cell]SurveyCell, used map[domain.Cell]bool) bool {
	in, door := pad(r, -1), baitDoor(r)
	for _, c := range rectCells(r) {
		sc, ok := cells[c]
		if !ok || !sc.ThickRoof || sc.Built || used[c] {
			return false
		}
		open := sc.Walkable && !sc.Rock
		if !open && (!sc.Rock || contains(in, c) || c == door) {
			return false
		}
	}
	return true
}

// baitDoor is the middle cell of the footprint's south wall.
func baitDoor(r Rectangle) domain.Cell { return domain.Cell{X: r.X + r.Width/2, Z: r.Z} }

// BaitRoomSections is one section building the bait room: a wall (stuff
// left for admission) on each bait-wall cell, a door, and a floor with its
// entry cell kept clear. The rest of the floor holds flammable furniture
// around an incendiary IED at its centre, or spike traps throughout when
// jelly is wanted.
func BaitRoomSections(plan LayoutPlan, wall, door, flammable string, jelly bool) ([]PerimeterSection, error) {
	sec := PerimeterSection{Name: DefenseTierName(TierBaitPrefix + "00")}
	add := func(def string, c domain.Cell) error {
		b, err := domain.NewBuilding(def, c, domain.North, "")
		if err == nil {
			sec.Buildings = append(sec.Buildings, b)
		}
		return err
	}
	var area Rectangle
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReserveBaitRoom:
			area = r.Area
		case ReserveBaitWall:
			for _, c := range rectCells(r.Area) {
				if err := add(wall, c); err != nil {
					return nil, err
				}
			}
		}
	}
	if area.Width == 0 {
		return nil, nil
	}
	in, doorCell := pad(area, -1), baitDoor(area)
	entry := domain.Cell{X: doorCell.X, Z: doorCell.Z + 1}
	centre := domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height/2}
	if err := add(door, doorCell); err != nil {
		return nil, err
	}
	for _, c := range rectCells(in) {
		def := flammable
		switch {
		case c == entry:
			continue
		case jelly:
			def = BaitTrap
		case c == centre:
			def = BaitIED
		}
		if err := add(def, c); err != nil {
			return nil, err
		}
	}
	return []PerimeterSection{sec}, nil
}
