package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Room construction to plan shapes (#787, C3): a room builder raises the
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
	// stuff is the stuff of each standing wall, where the census names one.
	stuff map[domain.Cell]string
}

// GroundOf reads the walls and doors out of the colony's built buildings.
func GroundOf(buildings []CurrentBuilding) GroundCensus {
	g := GroundCensus{walls: map[domain.Cell]bool{}, doors: map[domain.Cell]bool{}, stuff: map[domain.Cell]string{}}
	for _, b := range buildings {
		def := b.Building.Definition()
		for _, c := range b.Cells {
			switch {
			case def == "Wall":
				g.walls[c] = true
				g.stuff[c] = b.Building.Stuff()
			case strings.Contains(strings.ToLower(def), "door"):
				g.doors[c] = true
			}
		}
	}
	return g
}

// GroundMatches is true when r's wall ring and doors stand as planned: a wall
// on every ring cell, a door on each cell the plan holds one (ShellDoors) and
// none elsewhere on the ring. Geometry only; floors and furniture are separate
// diffs. No census room need be enclosed yet (a ring still unroofed matches).
func (p LayoutPlan) GroundMatches(r PlannedRoom, g GroundCensus) bool {
	doors := map[domain.Cell]bool{}
	for _, d := range p.ShellDoors(r) {
		doors[d] = true
	}
	ring := roomWalls(r)
	for _, c := range rectCells(ring) {
		if !onRing(c, ring) {
			continue
		}
		if doors[c] != g.doors[c] || !doors[c] && !g.walls[c] {
			return false
		}
	}
	return true
}

// CensusRoomIn is the census room standing enclosed on the planned
// room's centre cell and no larger than its interior: the lookup for the
// room's role, quality and temperature. Whether the room is built is
// GroundMatches.
func CensusRoomIn(r PlannedRoom, rooms RoomObservation) (Room, bool) {
	centre := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
	for _, room := range rooms.Rooms {
		if len(room.Cells) > int(r.Interior.Width*r.Interior.Height) {
			continue
		}
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed {
			continue
		}
		for _, c := range room.Cells {
			if c == centre {
				return room, true
			}
		}
	}
	return Room{}, false
}

// Footprint is the room's shell: its interior walled round, the door where
// the plan put it.
func (r PlannedRoom) Footprint() (domain.RoomFootprint, error) {
	in := r.Interior
	cells := make([]domain.Cell, 0, int(in.Width*in.Height))
	for z := in.Z; z < in.Z+in.Height; z++ {
		for x := in.X; x < in.X+in.Width; x++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
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
// ring standing yet (#835): the kitchen, freezer or jail its owning
// goal shells before furnishing. A dug room is mined out first (#836).
func (p LayoutPlan) NextPlannedRoom(role PlannedRole, ground GroundCensus) (PlannedRoom, bool) {
	for _, r := range p.AllRooms() {
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
// r's ring (the kitchen's side of the freezer door, #835; the barn's side of the
// vet room's, which is no AllRooms room, #2114).
func (p LayoutPlan) ShellDoors(r PlannedRoom) []domain.Cell {
	doors := []domain.Cell{r.Door}
	for _, d := range r.Doors {
		doors = append(doors, d.Cell)
	}
	if r.Link != nil {
		doors = append(doors, *r.Link)
	}
	in := r.Interior
	for _, o := range p.roomsWithHerd() {
		if o.Link == nil || o.Interior == in {
			continue
		}
		l := *o.Link
		onX := l.X == in.X-1 || l.X == in.X+in.Width
		onZ := l.Z == in.Z-1 || l.Z == in.Z+in.Height
		if onX && l.Z >= in.Z && l.Z < in.Z+in.Height || onZ && l.X >= in.X && l.X < in.X+in.Width {
			doors = append(doors, l)
		}
	}
	return doors
}
