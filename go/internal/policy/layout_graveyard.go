package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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

// graveSlot is the graveyard's first template slot no building stands on.
func graveSlot(r PlannedRoom, taken map[domain.Cell]bool) (InteriorPiece, bool) {
slots:
	for i, slot := range GraveyardSlots(r.Interior) {
		for _, c := range rectCells(slot) {
			if taken[c] {
				continue slots
			}
		}
		return NewInteriorPiece(fmt.Sprintf("grave-%d", i), GraveDefinition, domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: slot.X, Z: slot.Z}), true
	}
	return InteriorPiece{}, false
}

// GravesStanding counts the graves standing in the planned graveyards and the
// slots those graveyards hold: the capacity the further-graveyard request
// reads (GraveyardsWanted).
func GravesStanding(plan LayoutPlan, built []CurrentBuilding) (standing, slots int) {
	rooms := plan.roomsOf(PlannedGraveyard)
	for _, b := range built {
		if b.Building.Definition() != GraveDefinition {
			continue
		}
		for _, r := range rooms {
			if len(b.Cells) > 0 && rectContains(r.Interior, b.Cells[0]) {
				standing++
				break
			}
		}
	}
	for _, r := range rooms {
		slots += len(GraveyardSlots(r.Interior))
	}
	return standing, slots
}

// GraveyardsOwed is the graveyards demand asks for that plan lacks (#2217).
func GraveyardsOwed(plan LayoutPlan, demand RoomDemand) int {
	return max(demand.Graveyards-len(plan.roomsOf(PlannedGraveyard)), 0)
}

// growGraveyards adds the graveyards demand asks for and plan lacks. A further
// graveyard is an Outdoor room of the first one's size, sited like the
// outskirts cluster (outskirtsCandidates: outskirtsGap clear of every room,
// off the growth lines, walkable from the core) and, of the sites that fit,
// the nearest to the cluster. Its gate faces south like the cluster's yards
// and its fence ring is built through the same path as any Outdoor room. No
// room moves. It reports whether a graveyard was added; a graveyard that fits
// nowhere is left out.
func growGraveyards(plan LayoutPlan, demand RoomDemand) (LayoutPlan, bool) {
	owed := GraveyardsOwed(plan, demand)
	added := false
	for ; owed > 0; owed-- {
		best, _, _, _ := outskirtsCandidates(plan, GraveyardW+2, GraveyardH+2)
		area, ok := nearestSite(best, plan)
		if !ok {
			break
		}
		in := Rectangle{X: area.X + 1, Z: area.Z + 1, Width: GraveyardW, Height: GraveyardH}
		slot := OutskirtsSlot{Outline: area, Interior: in, Door: domain.Cell{X: in.X + in.Width/2, Z: area.Z}, DoorRot: domain.South}
		plan.Rooms = append(slices.Clone(plan.Rooms), slotRoom(PlannedGraveyard, slot))
		added = true
	}
	return plan, added
}

// nearestSite is the candidate nearest (by centre distance) to the outskirts
// cluster, or to the core when the plan holds none.
func nearestSite(sites map[domain.Rotation]Rectangle, plan LayoutPlan) (Rectangle, bool) {
	from, has := plan.OutskirtsArea()
	if !has {
		from, has = plan.CoreBounds()
	}
	if !has {
		return Rectangle{}, false
	}
	var out Rectangle
	found, best := false, int32(0)
	for _, side := range outskirtsSides {
		s, ok := sites[side]
		if !ok {
			continue
		}
		dx := (s.X + s.Width/2) - (from.X + from.Width/2)
		dz := (s.Z + s.Height/2) - (from.Z + from.Height/2)
		if d := dx*dx + dz*dz; !found || d < best {
			out, found, best = s, true, d
		}
	}
	return out, found
}
