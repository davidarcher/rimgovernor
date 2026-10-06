package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The outskirts cluster's composition (#2185, epic #2176). The one
// ReserveOutskirts outline holds four rooms in fixed slots, so the tomb and
// morgue (#2185), the graveyard (#2186) and the waste yard with its incinerator
// (#2187) never collide. Every rectangle below is an outline: the room's
// interior plus its one-cell ring (walls, or the yards' fence). Rooms stand
// apart, one cell of ground between neighbours, and every door faces
// the lane: a one-cell strip along the cluster's middle, open at both ends of
// the outline.
//
//	z ^   +--------------+--------------+
//	  |   | graveyard    | waste yard   |  row 1: Outdoor rooms, gates face the lane
//	  |   |              |   [incin.]   |
//	  |   +--------------+--------------+
//	  |   ................lane............
//	  |   +-------+ +-------+
//	  |   | tomb  | |morgue |               row 0: walled rooms, doors face the lane
//	  |   +-------+ +-------+
//	  +-------------------------------------> x
//
// Offsets are from the outline's south-west corner:
//   - tomb outline (0,0), interior 5x5 (coreRoomSize[PlannedTomb]);
//   - morgue outline (tombW+1, 0), interior 5x4 (coreRoomSize[PlannedMorgue]);
//     both rooms' back walls lie on the outline's south edge, so their cooler
//     exhaust (the cell behind the back wall) falls outside the reservation;
//   - the lane is the row at z = row 0's height;
//   - graveyard outline (0, row 0's height + 1), interior GraveyardInterior;
//   - waste yard outline (graveyard width + 1, same z), interior WasteYardInterior,
//     with the incinerator's 5x5 outline (IncineratorOutline) in its
//     north-east interior corner, away from the gate.
//
// The outline is OutskirtsSize wide and high: the wider of the two rows, and
// row 0, the lane and the taller yard. Rooms placed in a slot keep the outline
// whole, so the siting's 6-cell clearance from living rooms (outskirtsGap)
// holds for every room.

const (
	// GraveyardW and GraveyardH are the graveyard's interior (#2186): the
	// 12-grave template of GraveyardSlots fills it.
	GraveyardW int32 = 11
	GraveyardH int32 = 7
	// WasteYardW and WasteYardH are the waste yard's interior (#2187): 77
	// cells, the incinerator room takes a corner, the dump the rest.
	WasteYardW int32 = 11
	WasteYardH int32 = 7
	// IncineratorOutline is the incinerator room's walled outline side: a 3x3
	// interior and its ring.
	IncineratorOutline int32 = 5
)

// PlannedWasteYard is the waste yard's plan role (#2187): an Outdoor room that
// holds the dump zone's ground and the incinerator room.
const PlannedWasteYard PlannedRole = "waste_yard"

// OutskirtsSlot is one room's slot: the outline it stands in, its interior, and
// the wall cell its door (an Outdoor room's gate) takes with the side it faces.
type OutskirtsSlot struct {
	Outline, Interior Rectangle
	Door              domain.Cell
	DoorRot           domain.Rotation
}

// OutskirtsLayout is the slots of one outskirts outline.
type OutskirtsLayout struct {
	Tomb, Morgue, Graveyard, WasteYard OutskirtsSlot
	// Incinerator is the incinerator room's outline inside the waste yard's
	// interior.
	Incinerator Rectangle
}

// outskirtsRow0 is the walled rooms' row height: the taller of the tomb and morgue.
func outskirtsRow0() int32 {
	return max(coreRoomSize[PlannedTomb][1], coreRoomSize[PlannedMorgue][1]) + 2
}

// OutskirtsSize is the outline (width, height) the outskirts reserves: pass it
// as RoomGrowth.Outskirts.
func OutskirtsSize() [2]int32 {
	tomb, morgue := coreRoomSize[PlannedTomb], coreRoomSize[PlannedMorgue]
	row0 := tomb[0] + 2 + 1 + morgue[0] + 2
	row1 := GraveyardW + 2 + 1 + WasteYardW + 2
	return [2]int32{max(row0, row1), outskirtsRow0() + 1 + max(GraveyardH, WasteYardH) + 2}
}

// OutskirtsSlots is the slots of the outskirts outline area, anchored at its
// south-west corner; false when area is smaller than OutskirtsSize. It is the
// one place the cluster is composed: the rooms #2185 to #2187 place stand in
// these slots.
func OutskirtsSlots(area Rectangle) (OutskirtsLayout, bool) {
	size := OutskirtsSize()
	if area.Width < size[0] || area.Height < size[1] {
		return OutskirtsLayout{}, false
	}
	at := func(x, z, w, h int32) Rectangle { return Rectangle{X: area.X + x, Z: area.Z + z, Width: w, Height: h} }
	walled := func(outline Rectangle) OutskirtsSlot {
		in := Rectangle{X: outline.X + 1, Z: outline.Z + 1, Width: outline.Width - 2, Height: outline.Height - 2}
		return OutskirtsSlot{Outline: outline, Interior: in, Door: domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height}, DoorRot: domain.North}
	}
	yard := func(outline Rectangle) OutskirtsSlot {
		s := walled(outline)
		s.Door, s.DoorRot = domain.Cell{X: s.Interior.X + s.Interior.Width/2, Z: outline.Z}, domain.South
		return s
	}
	tomb, morgue := coreRoomSize[PlannedTomb], coreRoomSize[PlannedMorgue]
	row1 := outskirtsRow0() + 1
	var l OutskirtsLayout
	l.Tomb = walled(at(0, 0, tomb[0]+2, tomb[1]+2))
	l.Morgue = walled(at(tomb[0]+3, 0, morgue[0]+2, morgue[1]+2))
	l.Graveyard = yard(at(0, row1, GraveyardW+2, GraveyardH+2))
	l.WasteYard = yard(at(GraveyardW+3, row1, WasteYardW+2, WasteYardH+2))
	in := l.WasteYard.Interior
	l.Incinerator = Rectangle{X: in.X + in.Width - IncineratorOutline, Z: in.Z + in.Height - IncineratorOutline, Width: IncineratorOutline, Height: IncineratorOutline}
	return l, true
}

// OutskirtsOwed reports whether plan lacks the outskirts cluster or its tomb,
// morgue, graveyard, waste yard or incinerator: a plan that already holds a room of a role
// elsewhere (a core tomb) is not owed another.
func OutskirtsOwed(plan LayoutPlan) bool {
	_, has := plan.OutskirtsArea()
	return !has || len(outskirtsMissing(plan, OutskirtsLayout{})) > 0
}

// outskirtsMissing are the roles the cluster owes plan, each with its room.
// The slots in l place them; with the zero layout only the roles are meaningful.
func outskirtsMissing(plan LayoutPlan, l OutskirtsLayout) []PlannedRoom {
	var out []PlannedRoom
	for _, want := range outskirtsRooms(l) {
		if !slices.ContainsFunc(plan.AllRooms(), func(r PlannedRoom) bool { return r.Role == want.Role }) {
			out = append(out, want)
		}
	}
	return out
}

// outskirtsRooms are the rooms the cluster holds in l's slots: the walled tomb
// and morgue, the graveyard, the waste yard's Outdoor room and the incinerator inside it.
func outskirtsRooms(l OutskirtsLayout) []PlannedRoom {
	return []PlannedRoom{
		slotRoom(PlannedTomb, l.Tomb),
		slotRoom(PlannedMorgue, l.Morgue),
		slotRoom(PlannedGraveyard, l.Graveyard),
		slotRoom(PlannedWasteYard, l.WasteYard),
		incineratorRoom(l.Incinerator),
	}
}

// slotRoom is the room of role standing in slot: walled, or an Outdoor fence
// and gate ring (no roof, no floor owed). Every Outdoor room of the cluster
// (the waste yard, the graveyard) is made through it.
func slotRoom(role PlannedRole, slot OutskirtsSlot) PlannedRoom {
	return PlannedRoom{Role: role, Interior: slot.Interior, Door: slot.Door, DoorRot: slot.DoorRot, Outdoor: role.IsOutdoor()}
}

// incineratorRoom is the walled 3x3 room in outline, its door on the west wall
// facing into the yard it stands in.
func incineratorRoom(outline Rectangle) PlannedRoom {
	in := Rectangle{X: outline.X + 1, Z: outline.Z + 1, Width: outline.Width - 2, Height: outline.Height - 2}
	return PlannedRoom{Role: PlannedIncinerator, Interior: in, Door: domain.Cell{X: in.X - 1, Z: in.Z + in.Height/2}, DoorRot: domain.West}
}

// growOutskirtsRooms places the tomb, the morgue, the graveyard, the waste yard and the
// incinerator in their slots of the cluster plan holds, unless the plan already
// holds a room of the role, and reserves each walled room a cooler exhaust. The
// slots are never on rock: the cluster is sited on open core ground, so no room
// is dug. It reports whether it added a room.
func growOutskirtsRooms(plan LayoutPlan, thick map[domain.Cell]bool) (LayoutPlan, bool) {
	area, has := plan.OutskirtsArea()
	if !has {
		return plan, false
	}
	l, ok := OutskirtsSlots(area)
	if !ok {
		return plan, false
	}
	missing := outskirtsMissing(plan, l)
	if len(missing) == 0 {
		return plan, false
	}
	plan.Rooms = append(slices.Clone(plan.Rooms), missing...)
	u := newUtilityGrid(plan)
	u.thick = thick
	reserveExhausts(u, &plan)
	return plan, true
}
