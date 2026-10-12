package policy

import (
	"maps"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Room construction to plan shapes: a room builder raises the
// planned room's exact rectangle and door instead of searching.

// PlannedRoleFor is the v2 plan role a builder's room role fills; false for
// a role the core plans no room for.
func PlannedRoleFor(role RoomRole) (PlannedRole, bool) {
	switch role {
	case RoomRoleBedroom:
		return PlannedBedroom, true
	case RoomRoleSuite:
		return PlannedSuite, true
	case RoomRoleShelter:
		return PlannedShelter, true
	case RoomRoleKitchen:
		return PlannedKitchen, true
	case RoomRoleDiningRoom:
		return PlannedDining, true
	case RoomRoleRecRoom:
		return PlannedRec, true
	case RoomRoleHospital:
		return PlannedHospital, true
	case RoomRolePrisonCell, RoomRolePrisonBarracks:
		return PlannedPrison, true
	case RoomRoleWorkshop:
		return PlannedWorkshop, true
	case RoomRoleStoreroom:
		return PlannedStorage, true
	case RoomRoleLaboratory:
		return PlannedLab, true
	case RoomRoleTomb:
		return PlannedTomb, true
	case RoomRoleThroneRoom:
		return PlannedThrone, true
	case RoomRoleNursery:
		return PlannedNursery, true
	case RoomRolePlayroom:
		return PlannedPlayroom, true
	case RoomRoleClassroom:
		return PlannedClassroom, true
	case RoomRoleWorshipRoom:
		return PlannedWorship, true
	case RoomRoleDeathrestChamber:
		return PlannedDeathrestChamber, true
	case RoomRoleContainmentCell:
		return PlannedContainmentCell, true
	case RoomRoleIsolationRoom:
		return PlannedIsolationRoom, true
	}
	return "", false
}

// GroundCensus is the walls and doors standing on the ground, by cell: the
// geometry a PlannedRoom's ring is matched against.
type GroundCensus struct {
	walls, doors map[domain.Cell]bool
	// fences and gates are the ring of an outdoor room (PlannedRoom.Outdoor).
	fences, gates map[domain.Cell]bool
	// flaps are the animal flaps standing, kept apart from doors: a
	// barn's wall takes one where a room's ring takes a door.
	flaps map[domain.Cell]bool
	// stuff is the stuff of each standing wall, where the census names one.
	stuff map[domain.Cell]string
}

// GroundOf reads the walls and doors out of the colony's built buildings.
func GroundOf(buildings []CurrentBuilding) GroundCensus { return GroundOfWithFlap(buildings, "") }

// GroundOfWithFlap is GroundOf with the animal flap's def (the catalog's,
// RoomFurniture.AnimalFlap) read as a flap, never as a door; "" reads none.
func GroundOfWithFlap(buildings []CurrentBuilding, flap string) GroundCensus {
	g := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}, fences: map[domain.Cell]bool{}, gates: map[domain.Cell]bool{}, flaps: map[domain.Cell]bool{}, stuff: map[domain.Cell]string{}}
	for _, b := range buildings {
		def := b.Building.Definition()
		for _, c := range b.Cells {
			switch {
			case flap != "" && def == flap:
				g.flaps[c] = true
			case def == PenFenceDefinition:
				g.fences[c] = true
			case def == PenGateDefinition:
				g.gates[c] = true
			case def == ShellWallDefinition:
				g.walls[c] = true
				g.stuff[c] = b.Building.Stuff()
			case strings.Contains(strings.ToLower(def), "door"):
				g.doors[c] = true
			}
		}
	}
	return g
}

// RingDefs are the defs r's ring is raised from: a wall and a door, or a fence
// and a gate round an outdoor room.
func (r PlannedRoom) RingDefs() (wall, door string) {
	if r.Outdoor {
		return PenFenceDefinition, PenGateDefinition
	}
	return ShellWallDefinition, ShellDoorDefinition
}

// withCells is a copy of set that can be written (nil reads as empty).
func withCells(set map[domain.Cell]bool) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(set))
	maps.Copy(out, set)
	return out
}

// ring is the standing walls and doors r's ring is matched against: an
// outdoor room's fences and gates; a barn's flap cells count as a door where a
// flap stands.
func (p LayoutPlan) ring(r PlannedRoom, g GroundCensus) (walls, doors map[domain.Cell]bool) {
	if r.Outdoor {
		return g.fences, g.gates
	}
	walls, doors = g.walls, g.doors
	if flaps := p.FlapCells(r); len(flaps) > 0 {
		doors = withCells(doors)
		for _, c := range flaps {
			doors[c] = g.flaps[c]
		}
	}
	return walls, doors
}

// GroundMatches is true when r's wall ring and doors stand as planned: a wall
// on every ring cell, a door on each cell the plan holds one (ShellDoors) and
// none elsewhere on the ring. Geometry only; floors and furniture are separate
// diffs. No census room need be enclosed yet (a ring still unroofed matches).
func (p LayoutPlan) GroundMatches(r PlannedRoom, g GroundCensus) bool {
	if r.Unfenced() {
		return true // no ring owed
	}
	doors := map[domain.Cell]bool{}
	for _, d := range p.ShellDoors(r) {
		doors[d] = true
	}
	walls, standing := p.ring(r, g)
	for _, c := range r.wallRing().Cells() {
		if doors[c] != standing[c] || !doors[c] && !walls[c] {
			return false
		}
	}
	if r.Airlock != nil {
		if !standing[r.Airlock.Outer] {
			return false
		}
		for _, c := range r.AirlockWalls() {
			if standing[c] || !walls[c] {
				return false
			}
		}
	}
	return true
}

// wallRing is the cells r's shell stands on: the region's wall ring
// (Region.Walls). A sparring ring and the unfenced roles with no walls of
// their own keep their rectangle behaviour: the border of the block they
// reserve (ringBlock, rangeBlock, the interior).
func (r PlannedRoom) wallRing() Region {
	if r.Unfenced() {
		return RectRegion(roomWalls(r)).Perimeter()
	}
	return r.Region.Walls()
}

// anchor is the region cell nearest its bounding-box centre (ties to the
// first in cell order): the centre of a rectangle, a cell inside an L or a
// holed region whose centre is not itself part of it.
func (g Region) anchor() domain.Cell {
	b := g.Bounds()
	cx, cz := int64(2*b.X+b.Width), int64(2*b.Z+b.Height) // centre doubled
	centre := domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}
	if g.Contains(centre) {
		return centre
	}
	best, bestD := domain.Cell{}, int64(-1)
	for _, c := range g.Cells() {
		dx, dz := int64(2*c.X)+1-cx, int64(2*c.Z)+1-cz
		if d := dx*dx + dz*dz; bestD < 0 || d < bestD {
			best, bestD = c, d
		}
	}
	return best
}

// CensusRoomIn is the census room standing enclosed on the planned room's
// anchor cell and lying wholly inside its region: the lookup for the room's
// role, quality and temperature. A census room that spills a cell outside the
// region (one cell off, or merged with a neighbour) is not the planned room.
// Whether the room is built is GroundMatches.
func CensusRoomIn(r PlannedRoom, rooms RoomObservation) (Room, bool) {
	if r.Region.Empty() {
		return Room{}, false
	}
	anchor := r.Region.anchor()
	for _, room := range rooms.Rooms {
		if len(room.Cells) > r.Region.Area() {
			continue
		}
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed {
			continue
		}
		hit, within := false, true
		for _, c := range room.Cells {
			hit = hit || c == anchor
			within = within && r.Region.Contains(c)
		}
		if hit && within {
			return room, true
		}
	}
	return Room{}, false
}

// Footprint is the room's shell: its region walled round, the door where
// the plan put it. Any region the footprint rules accept (4-connected, at most
// 3844 cells, a ring of at most 255) shells, rectangular or not.
func (r PlannedRoom) Footprint() (domain.RoomFootprint, error) {
	cells := r.Region.Cells()
	var extra []domain.RoomDoor
	for _, d := range r.Doors {
		extra = append(extra, domain.RoomDoor{Cell: d.Cell, Entrance: d.Rot})
	}
	return domain.NewRoomFootprint(cells, r.Door, r.DoorRot, extra...)
}

// PlannedShells lists the plan's rooms for role as shells, in plan order;
// a room whose shell is invalid is skipped.
func (p LayoutPlan) PlannedShells(role RoomRole) []domain.RoomFootprint {
	want, ok := PlannedRoleFor(role)
	if !ok {
		return nil
	}
	var shells []domain.RoomFootprint
	for _, r := range p.AllRooms() {
		if r.Role != want {
			continue
		}
		if shell, err := r.Footprint(); err == nil {
			shells = append(shells, shell)
		}
	}
	return shells
}

// NextPlannedRoom is the plan's first open-ground room of role with no
// ring standing yet: the kitchen, freezer or jail its owning
// goal shells before furnishing. A dug room is mined out first.
func (p LayoutPlan) NextPlannedRoom(role PlannedRole, ground GroundCensus) (PlannedRoom, bool) {
	rooms := p.AllRooms()
	if role == PlannedYard {
		// Yards are reservations viewed as rooms, not plan rooms.
		rooms = p.YardRooms()
	}
	for _, r := range rooms {
		if r.Role != role {
			continue
		}
		if !p.GroundMatches(r, ground) {
			return r, true
		}
	}
	return PlannedRoom{}, false
}

// ShellDoors is the cells of r's ring that take a door rather than a
// wall: its own doors, its Link, and any other room's Link that lies in
// r's ring (the kitchen's side of the freezer door; the barn's side of the
// vet room's, which is no AllRooms room).
func (p LayoutPlan) ShellDoors(r PlannedRoom) []domain.Cell {
	if r.Unfenced() {
		return nil
	}
	doors := []domain.Cell{r.Door}
	for _, d := range r.Doors {
		doors = append(doors, d.Cell)
	}
	if r.Link != nil {
		doors = append(doors, *r.Link)
	}
	doors = append(doors, p.LinkCells(r.ID)...)
	if r.Airlock != nil {
		doors = append(doors, r.Airlock.Outer)
	}
	doors = append(doors, p.FlapCells(r)...)
	ring := r.Region.Walls()
	for _, o := range p.roomsWithHerd() {
		if o.Link == nil || slices.Equal(o.Region.Runs, r.Region.Runs) {
			continue
		}
		if l := *o.Link; ring.Contains(l) && r.Region.adjacent(l) {
			doors = append(doors, l)
		}
	}
	return doors
}
