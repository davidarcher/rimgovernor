package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The graveyard (#2186, epic #2176): a planned Outdoor room in the outskirts
// cluster. Its ring is a fence and a gate, its interior has no roof and owes
// no floor (graves need diggable soil, not a constructed floor), and it never
// grows: a full graveyard asks for a further one (#2196).
//
// The interior template packs GraveyardGraves plain graves (GraveDefinition,
// 1x2) in two bands of GraveyardColumns columns. Graves stand on even columns,
// so every grave's long sides face an aisle column (or the room's edge on one
// side), and three aisle rows (the gate row, the row between the bands and
// the top row) join the aisle columns to the gate.
//
//	z=6  . . . . . . . . . . .   aisle
//	z=5  G . G . G . G . G . G
//	z=4  G . G . G . G . G . G   band 1
//	z=3  . . . . . . . . . . .   aisle
//	z=2  G . G . G . G . G . G
//	z=1  G . G . G . G . G . G   band 0
//	z=0  . . . . . . . . . . .   aisle (the gate is in the south wall below x=5)

const (
	// GraveyardGraves is the graves the graveyard is sized for.
	GraveyardGraves = 12
	// GraveyardColumns is the grave columns in each of the two bands.
	GraveyardColumns = GraveyardGraves / 2
)

// graveBandRows are the interior rows (from the south) the two bands start at.
var graveBandRows = [2]int32{1, 4}

// GraveyardSlots are the grave rectangles (1 wide, 2 deep) of a graveyard
// whose interior is in, in plan order; nil when in is too small for the
// template.
func GraveyardSlots(in Rectangle) []Rectangle {
	if in.Width < 2*GraveyardColumns-1 || in.Height < graveBandRows[1]+3 {
		return nil
	}
	var out []Rectangle
	for _, row := range graveBandRows {
		for col := int32(0); col < GraveyardColumns; col++ {
			out = append(out, Rectangle{X: in.X + 2*col, Z: in.Z + row, Width: 1, Height: 2})
		}
	}
	return out
}

// outdoorRoles are the roles planned as an Outdoor room (PlannedRoom.Outdoor):
// a fence and a gate ring, no roof, no floor owed.
var outdoorRoles = map[PlannedRole]bool{PlannedPen: true, PlannedGraveyard: true, PlannedWasteYard: true, PlannedYard: true}

// IsOutdoor reports whether a room of role is planned as an Outdoor room.
func (r PlannedRole) IsOutdoor() bool { return outdoorRoles[r] }

// gateCell is the interior cell just inside an outdoor room's gate.
func (r PlannedRoom) gateCell() domain.Cell {
	c := r.Door
	switch r.DoorRot {
	case domain.North:
		c.Z--
	case domain.South:
		c.Z++
	case domain.East:
		c.X--
	case domain.West:
		c.X++
	}
	return c
}
