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
	// WasteYardW and WasteYardH are the waste yard's interior (#2187); the
	// incinerator room takes a corner, the dump the rest. #2187 may adjust them.
	WasteYardW int32 = 11
	WasteYardH int32 = 7
	// IncineratorOutline is the incinerator room's walled outline side: a 3x3
	// interior and its ring.
	IncineratorOutline int32 = 5
)

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
// morgue or graveyard: a plan that already holds a room of a role elsewhere (a core
// tomb) is not owed another.
func OutskirtsOwed(plan LayoutPlan) bool {
	_, has := plan.OutskirtsArea()
	return !has || len(plan.roomsOf(PlannedTomb)) == 0 || len(plan.roomsOf(PlannedMorgue)) == 0 || len(plan.roomsOf(PlannedGraveyard)) == 0
}

// growOutskirtsRooms places the tomb, the morgue and the graveyard in their slots of the
// cluster plan holds, unless the plan already holds a room of the role, and
// reserves each a cooler exhaust. The slots are never on rock: the cluster is
// sited on open core ground, so neither room is dug. It reports whether it
// added a room.
func growOutskirtsRooms(plan LayoutPlan, thick map[domain.Cell]bool) (LayoutPlan, bool) {
	area, has := plan.OutskirtsArea()
	if !has {
		return plan, false
	}
	l, ok := OutskirtsSlots(area)
	if !ok {
		return plan, false
	}
	added := false
	rooms := slices.Clone(plan.Rooms)
	for _, want := range []struct {
		role PlannedRole
		slot OutskirtsSlot
	}{{PlannedTomb, l.Tomb}, {PlannedMorgue, l.Morgue}, {PlannedGraveyard, l.Graveyard}} {
		if slices.ContainsFunc(plan.AllRooms(), func(r PlannedRoom) bool { return r.Role == want.role }) {
			continue
		}
		rooms = append(rooms, PlannedRoom{Role: want.role, Interior: want.slot.Interior, Door: want.slot.Door, DoorRot: want.slot.DoorRot, Outdoor: want.role.IsOutdoor()})
		added = true
	}
	if !added {
		return plan, false
	}
	plan.Rooms = rooms
	u := newUtilityGrid(plan)
	u.thick = thick
	reserveExhausts(u, &plan)
	return plan, true
}
