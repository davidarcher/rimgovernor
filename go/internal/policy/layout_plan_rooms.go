package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Room queries over a LayoutPlan: which rooms stand built,
// which roles are ended, and dropping rooms by id.

// ChildrenOf are the rooms attached to room id (an airlock, a porch), in plan
// order; nil for a room with none.
func (p LayoutPlan) ChildrenOf(id int32) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Rooms {
		if id != 0 && r.Parent == id {
			out = append(out, r)
		}
	}
	return out
}

func (p LayoutPlan) roomsOf(role PlannedRole) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Rooms {
		if r.Role == role {
			out = append(out, r)
		}
	}
	return out
}

// BuiltRooms are the interiors of the plan's rooms whose ring stands as
// planned (GroundMatches).
func BuiltRooms(plan LayoutPlan, ground GroundCensus) map[int32]bool {
	built := map[int32]bool{}
	for _, r := range plan.Rooms {
		if plan.GroundMatches(r, ground) {
			built[r.ID] = true
		}
	}
	return built
}

// FixedRooms are the interiors of the plan's rooms with anything of ours on
// them: its ring standing as planned, or a cell of occupied (a building,
// blueprint, frame, edifice, doorway or journal claim) on its walls or
// floor. A replan must not move a fixed room; an untouched planned room is
// not fixed.
func FixedRooms(plan LayoutPlan, ground GroundCensus, occupied map[domain.Cell]bool) map[int32]bool {
	fixed := BuiltRooms(plan, ground)
	for _, r := range plan.Rooms {
		if !fixed[r.ID] && rectHits(roomWalls(r), occupied) {
			fixed[r.ID] = true
		}
	}
	return fixed
}

// allPins is every room of plan.
func allPins(plan LayoutPlan) map[int32]bool {
	pins := map[int32]bool{}
	for _, r := range plan.Rooms {
		pins[r.ID] = true
	}
	return pins
}

func bedroomCount(p LayoutPlan) int {
	n := 0
	for _, r := range p.Rooms {
		if r.Role == PlannedBedroom {
			n++
		}
	}
	return n
}

// InFlightRoomCells are the walls of the plan's rooms an open journal plan
// is working on, keyed by the room's interior origin as the plan's method
// ids are: added to the occupied cells, they make such a room fixed
// before any building of it is accepted.
func InFlightRoomCells(plan LayoutPlan, origins map[domain.Cell]bool) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range plan.Rooms {
		if origins[domain.Cell{X: r.Region.Bounds().X, Z: r.Region.Bounds().Z}] {
			for _, c := range rectCells(roomWalls(r)) {
				out[c] = true
			}
		}
	}
	return out
}

// InFlightRooms are the interiors of the plan's rooms an open journal plan is
// working on, keyed by origin as InFlightRoomCells is.
func InFlightRooms(plan LayoutPlan, origins map[domain.Cell]bool) map[int32]bool {
	out := map[int32]bool{}
	for _, r := range plan.Rooms {
		if origins[domain.Cell{X: r.Region.Bounds().X, Z: r.Region.Bounds().Z}] {
			out[r.ID] = true
		}
	}
	return out
}

// RoomsOfRoles is how many planned rooms hold one of roles.
func RoomsOfRoles(plan LayoutPlan, roles []PlannedRole) int {
	n := 0
	for _, role := range roles {
		n += len(plan.roomsOf(role))
	}
	return n
}

func withoutRooms(rooms []PlannedRoom, drop map[int32]bool) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range rooms {
		if !drop[r.ID] {
			out = append(out, r)
		}
	}
	return out
}

// Ended needs: an unbuilt planned child, worship,
// deathrest or containment room whose need has ended leaves the plan. A built
// room stays (only duplicate-room retirement removes one); an unknown fact is
// never an ended need.

// EndedRoomRoles are the add-on roles whose need is known to be gone: every
// pawn's stage (and, for the deathrest chamber, deathrest) is known and none
// owes the room; the ideoligion is known and requires no building; the
// containment demand is known to be zero entities. needs are the rooms still
// owed.
func EndedRoomRoles(pawns domain.Fact[[]WorkPawn], ideology domain.Fact[Ideoligion], containment ContainmentPlanning, isolation IsolationPlanning, needs []ChildRoomNeed) []PlannedRole {
	owed := map[PlannedRole]bool{}
	for _, n := range needs {
		owed[n.Module] = true
	}
	var ended []PlannedRole
	add := func(known bool, roles ...PlannedRole) {
		for _, role := range roles {
			if known && !owed[role] {
				ended = append(ended, role)
			}
		}
	}
	list, listKnown := pawns.Value()
	stages, deathrests := listKnown, listKnown
	for _, p := range list {
		bt, ok := p.Biotech.Value()
		if !ok {
			stages, deathrests = false, false
			break
		}
		if _, ok := bt.DevelopmentalStage.Value(); !ok {
			stages = false
		}
		if _, ok := bt.Deathrest.Value(); !ok {
			deathrests = false
		}
	}
	add(stages && len(list) > 0, PlannedNursery, PlannedPlayroom, PlannedClassroom)
	add(deathrests && len(list) > 0, PlannedDeathrestChamber)
	_, ideoKnown := ideology.Value()
	add(ideoKnown, PlannedWorship)
	demand, demandKnown := containment.Demand.Value()
	add(demandKnown && demand.Entities == 0, PlannedContainmentCell)
	isolated, isolatedKnown := isolation.Pawns.Value()
	add(isolatedKnown && len(isolated) == 0, PlannedIsolationRoom)
	return ended
}

// RoomsInUse are the interiors of the plan's rooms that stand as planned or
// hold a building of furniture's definitions.
func RoomsInUse(plan LayoutPlan, built []CurrentBuilding, furniture []FurnitureDefinition) map[int32]bool {
	inUse := BuiltRooms(plan, GroundOf(built))
	var names []string
	for _, d := range furniture {
		names = append(names, d.Name)
	}
	for _, r := range plan.Rooms {
		if !inUse[r.ID] && standingChildPieces(r, names, built) > 0 {
			inUse[r.ID] = true
		}
	}
	return inUse
}

// dropRooms removes the rooms with the interiors in drop from plan.
// It reports whether there was any.
func dropRooms(plan LayoutPlan, drop map[int32]bool) (LayoutPlan, bool) {
	if len(drop) == 0 {
		return plan, false
	}
	for _, r := range plan.Rooms {
		if r.Parent != 0 && drop[r.Parent] {
			drop[r.ID] = true // a child leaves with its parent
		}
	}
	plan.Rooms = withoutRooms(plan.Rooms, drop)
	// A door joins two rooms: one that leaves takes its Links entries with it.
	plan.Links = slices.DeleteFunc(slices.Clone(plan.Links), func(l RoomLink) bool { return drop[l.A] || drop[l.B] })
	return plan, true
}
