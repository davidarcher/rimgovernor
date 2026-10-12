package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Infestation bait room. Hives pick dark open ground
// under overhead mountain; once mountain hardening walls off the pockets and lights
// the base, one dark room far from the base is left for them, stocked
// with cheap flammables and an incendiary IED (or spike traps when insect
// jelly is wanted) so the hive burns or bleeds where no colonist lives.
//
// The room is a NodeBaitRoom site node produced after the ring node (#2925):
// its footprint is the floor and its wall ring, and it carries PlannedBaitRoom
// so the overlay draws it as a room. It is not a PlannedRoom: it stands outside
// the base's room graph and is built by BaitRoomSections. The wall cells that
// are not natural rock are a NodeBaitWall node beside it.

// PlannedBaitRoom is the bait room's plan role, carried by its site node.
const PlannedBaitRoom PlannedRole = "bait_room"

// ReasonBaitNearest: the bait room stands on the thick-roof spot nearest the
// ring that keeps baitGap off it.
const ReasonBaitNearest = "bait_nearest_dark_spot"

const (
	// baitInterior is the side of the bait room's square floor.
	baitInterior int32 = 3
	// baitGap is how far outside the perimeter ring the bait
	// room's walls must stay. A judgment constant (DefTables "infestationReach").
	baitGap int32 = 20
	// TierBaitPrefix names the bait room's section under the perimeter's
	// prefix, so the perimeter recut builds and removes it with the wall.
	TierBaitPrefix = TierPerimeterPrefix + "bait-"
	// BaitIED and BaitTrap are the vanilla defs the bait room arms with.
	BaitIED  = "TrapIED_Incendiary"
	BaitTrap = "TrapSpike"
)

// PlanBaitRoom replaces plan's bait-room nodes with the footprint
// (wall ring included) of the square room nearest the perimeter ring that
// lies wholly under thick roof, at least baitGap outside the ring's
// bounds, on no built cell and no cell another room, hallway or
// site node uses, with an open floor and an open door cell at the
// middle of its south wall. Wall cells may be natural rock; the open ones
// form the bait-wall node. A plan without a perimeter, or a map
// with no such spot, holds none.
func PlanBaitRoom(plan LayoutPlan, s MapSurvey) LayoutPlan {
	plan = plan.withoutSites(NodeBaitRoom, NodeBaitWall)
	ring, ok := plan.Ring()
	if !ok {
		return plan
	}
	bounds := ring.Footprint.Bounds()
	used := map[domain.Cell]bool{}
	for _, r := range plan.Reserved() {
		for _, c := range r.Area.Cells() {
			used[c] = true
		}
	}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			used[c] = true
		}
	}
	for _, sg := range plan.Hallways() {
		for _, c := range rectCells(pad(rectOf(sg.From, sg.To), SpineWidth/2)) {
			used[c] = true
		}
	}
	cells := make(map[domain.Cell]SurveyCell, len(s.Cells))
	for _, c := range s.Cells {
		cells[c.Cell] = c
	}
	wi, _ := planInterior(plan, baitGap)
	near := wi.near(baitGap)
	centre := domain.Cell{X: bounds.X + bounds.Width/2, Z: bounds.Z + bounds.Height/2}
	side := baitInterior + 2
	var best Rectangle
	bestD := -1.0
	for _, c := range s.Cells {
		r := Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: side, Height: side}
		if baitNear(r, near) || !baitFits(r, cells, used) {
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
	room := newSite(string(NodeBaitRoom), NodeBaitRoom, RectRegion(best), ReasonBaitNearest)
	room.Role = PlannedBaitRoom
	plan.Sites = append(plan.Sites, room)
	in, door := pad(best, -1), baitDoor(best)
	var open []domain.Cell
	for _, c := range rectCells(best) {
		if !contains(in, c) && c != door && !cells[c].Rock {
			open = append(open, c)
		}
	}
	if len(open) > 0 {
		plan.Sites = append(plan.Sites, newSite(string(NodeBaitWall), NodeBaitWall, RegionOfRuns(cellRuns(open)), ReasonBaitNearest))
	}
	return plan
}

// baitNear reports whether any cell of r lies within baitGap of the ring
// or its inside: the traced ring, not its bounds.
func baitNear(r Rectangle, near func(domain.Cell) int32) bool {
	for _, c := range rectCells(r) {
		if near(c) >= 0 {
			return true
		}
	}
	return false
}

func baitFits(r Rectangle, cells map[domain.Cell]SurveyCell, used map[domain.Cell]bool) bool {
	in, door := pad(r, -1), baitDoor(r)
	for _, c := range rectCells(r) {
		sc, ok := cells[c]
		if !ok || !sc.ThickRoof || sc.Built || used[c] {
			return false
		}
		open := sc.Walkable && !sc.Rock && !sc.Hazard
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
	for _, r := range plan.LiveSites(NodeBaitRoom) {
		area = r.Footprint.Bounds()
	}
	if area.Width == 0 {
		return nil, nil
	}
	for _, r := range plan.LiveSites(NodeBaitWall) {
		for _, c := range r.Footprint.Cells() {
			if err := add(wall, c); err != nil {
				return nil, err
			}
		}
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
