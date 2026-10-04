package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Duplicate add-on rooms (#1823, epic #1819): the planner only ever added
// rooms, so a room it grew twice (nine worship rooms after a transposed
// frame) stayed in the plan. A role's rooms reduce to one; the rest leave
// the plan, and a built one is no longer planned ground, so clearance
// demolishes it as it does a dropped wing's. Rooms are never shrunk.

// addOnRoomRoles are the roles holding at most one planned room.
var addOnRoomRoles = []ModuleRole{ModuleWorship, ModuleNursery, ModulePlayroom, ModuleClassroom, ModuleDeathrestChamber, ModuleContainmentCell, ModuleIsolationRoom}

// ChildRoomShapes are the shapes of every need whose furniture resolves,
// planned or not.
func ChildRoomShapes(needs []ChildRoomNeed, defs []FurnitureDefinition) []ChildRoomShape {
	var out []ChildRoomShape
	for _, n := range needs {
		if s, ok := n.shape(defs); ok {
			out = append(out, s)
		}
	}
	return out
}

// BuiltRooms are the interiors of the plan's rooms with a census room
// standing in them.
func BuiltRooms(plan LayoutPlan, rooms RoomObservation) map[Rectangle]bool {
	built := map[Rectangle]bool{}
	for _, r := range plan.AllRooms() {
		if _, ok := PlannedRoomStanding(r, rooms); ok {
			built[r.Interior] = true
		}
	}
	return built
}

// FixedRooms are the interiors of the plan's rooms with anything of ours on
// them: a census room standing in one, or a cell of occupied (a building,
// blueprint, frame, edifice, doorway or journal claim) on its walls or
// floor. A replan must not move a fixed room; an untouched planned room is
// not fixed.
func FixedRooms(plan LayoutPlan, rooms RoomObservation, occupied map[domain.Cell]bool) map[Rectangle]bool {
	fixed := BuiltRooms(plan, rooms)
	for _, r := range plan.AllRooms() {
		if !fixed[r.Interior] && rectHits(roomWalls(r), occupied) {
			fixed[r.Interior] = true
		}
	}
	return fixed
}

// DuplicateRooms is how many add-on rooms the plan holds beyond one per role.
func DuplicateRooms(plan LayoutPlan) int {
	n := 0
	for _, role := range addOnRoomRoles {
		n += max(len(plan.roomsOf(role))-1, 0)
	}
	return n
}

func (p LayoutPlan) roomsOf(role ModuleRole) []LayoutRoom {
	var out []LayoutRoom
	for _, r := range p.AllRooms() {
		if r.Role == role {
			out = append(out, r)
		}
	}
	return out
}

// retireAddOnRooms drops the add-on rooms the plan no longer needs: the
// duplicates beyond one per role (when built is known) and the unbuilt rooms
// of an ended role (when inUse is known). It reports whether it dropped any.
func retireAddOnRooms(plan LayoutPlan, growth RoomGrowth) (LayoutPlan, bool) {
	drop := map[Rectangle]bool{}
	if growth.Built != nil {
		duplicateRooms(plan, growth.Shapes, growth.Built, drop)
	}
	if growth.InUse != nil {
		endedRooms(plan, growth.Ended, growth.InUse, drop)
	}
	return dropRooms(plan, drop)
}

// duplicateRooms adds to drop all but one room of each add-on role: a built
// room first, else the smallest that holds the role.s shape (shapes), else
// the smallest.
func duplicateRooms(plan LayoutPlan, shapes []ChildRoomShape, built map[Rectangle]bool, drop map[Rectangle]bool) {
	for _, role := range addOnRoomRoles {
		rooms := plan.roomsOf(role)
		if len(rooms) < 2 {
			continue
		}
		keep := -1
		for i, r := range rooms {
			if built[r.Interior] {
				keep = i
				break
			}
		}
		if keep < 0 {
			keep = smallestRoom(rooms, roleShape(shapes, role))
		}
		for i, r := range rooms {
			if i != keep {
				drop[r.Interior] = true
			}
		}
	}
}

func roleShape(shapes []ChildRoomShape, role ModuleRole) *ChildRoomShape {
	for i := range shapes {
		if shapes[i].Module == role {
			return &shapes[i]
		}
	}
	return nil
}

// smallestRoom is the index of the fewest-cell room holding shape, else of
// the fewest-cell room.
func smallestRoom(rooms []LayoutRoom, shape *ChildRoomShape) int {
	best, bestHolds := 0, false
	for i, r := range rooms {
		w, d := frameDims(r)
		holds := shape != nil && shape.holds(w, d)
		if i == 0 {
			best, bestHolds = 0, holds
			continue
		}
		if holds && !bestHolds || holds == bestHolds && r.Interior.Width*r.Interior.Height < rooms[best].Interior.Width*rooms[best].Interior.Height {
			best, bestHolds = i, holds
		}
	}
	return best
}

func withoutRooms(rooms []LayoutRoom, drop map[Rectangle]bool) []LayoutRoom {
	var out []LayoutRoom
	for _, r := range rooms {
		if !drop[r.Interior] {
			out = append(out, r)
		}
	}
	return out
}

// Ended needs (#1824, epic #1819): an unbuilt planned child, worship,
// deathrest or containment room whose need has ended leaves the plan. A built
// room stays (only #1823's duplicate rule retires one); an unknown fact is
// never an ended need.

// EndedRoomRoles are the add-on roles whose need is known to be gone: every
// pawn's stage (and, for the deathrest chamber, deathrest) is known and none
// owes the room; the ideoligion is known and requires no building; the
// containment demand is known to be zero entities. needs are the rooms still
// owed.
func EndedRoomRoles(pawns domain.Fact[[]WorkPawn], ideology domain.Fact[Ideoligion], containment ContainmentPlanning, isolation IsolationPlanning, needs []ChildRoomNeed) []ModuleRole {
	owed := map[ModuleRole]bool{}
	for _, n := range needs {
		owed[n.Module] = true
	}
	var ended []ModuleRole
	add := func(known bool, roles ...ModuleRole) {
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
	add(stages && len(list) > 0, ModuleNursery, ModulePlayroom, ModuleClassroom)
	add(deathrests && len(list) > 0, ModuleDeathrestChamber)
	_, ideoKnown := ideology.Value()
	add(ideoKnown, ModuleWorship)
	demand, demandKnown := containment.Demand.Value()
	add(demandKnown && demand.Entities == 0, ModuleContainmentCell)
	isolated, isolatedKnown := isolation.Pawns.Value()
	add(isolatedKnown && len(isolated) == 0, ModuleIsolationRoom)
	return ended
}

// RoomsInUse are the interiors of the plan's rooms that stand as a census
// room or hold a building of furniture's definitions.
func RoomsInUse(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, furniture []FurnitureDefinition) map[Rectangle]bool {
	inUse := BuiltRooms(plan, rooms)
	var names []string
	for _, d := range furniture {
		names = append(names, d.Name)
	}
	for _, r := range plan.AllRooms() {
		if !inUse[r.Interior] && standingChildPieces(r, names, built) > 0 {
			inUse[r.Interior] = true
		}
	}
	return inUse
}

// endedRooms adds to drop each planned room of an ended role that is not in
// use.
func endedRooms(plan LayoutPlan, ended []ModuleRole, inUse map[Rectangle]bool, drop map[Rectangle]bool) {
	for _, role := range ended {
		for _, r := range plan.roomsOf(role) {
			if !inUse[r.Interior] {
				drop[r.Interior] = true
			}
		}
	}
}

// dropRooms removes the rooms with the interiors in drop from plan and its
// wings. It reports whether there was any.
func dropRooms(plan LayoutPlan, drop map[Rectangle]bool) (LayoutPlan, bool) {
	if len(drop) == 0 {
		return plan, false
	}
	plan.Rooms = withoutRooms(plan.Rooms, drop)
	plan.Wings = append([]Wing(nil), plan.Wings...)
	for i := range plan.Wings {
		plan.Wings[i].Rooms = withoutRooms(plan.Wings[i].Rooms, drop)
	}
	return plan, true
}

// Surplus rooms (#1825, epic #1819): a throne room the title outgrew, a gear
// room whose demand is gone and an extra storage room with no demand leave
// the plan while unbuilt. Rooms are never shrunk; a smaller built throne room
// stays until a room holding the title's area is itself built, then goes to
// clearance. The core storage room and built gear and storage rooms stay.

// SurplusRoomsPossible reports whether retireSurplusRooms could drop a room
// of plan: several throne rooms under a title that asks for one, a gear room
// whose demand reads false, or an extra storage room under an idle reading.
func SurplusRoomsPossible(plan LayoutPlan, throneMin int, demand RoomDemand) bool {
	if throneMin > 0 && len(plan.roomsOf(ModuleThrone)) > 1 {
		return true
	}
	if demand.Known && (!demand.Armory && len(plan.roomsOf(ModuleArmory)) > 0 || !demand.Wardrobe && len(plan.roomsOf(ModuleWardrobe)) > 0) {
		return true
	}
	return demand.StorageIdle && len(plan.roomsOf(ModuleStorage)) > 1
}

// retireSurplusRooms drops the surplus rooms of growth that are unbuilt, plus
// the built throne rooms a built larger one replaces. It reports whether it
// dropped any.
func retireSurplusRooms(plan LayoutPlan, growth RoomGrowth, built map[Rectangle]bool) (LayoutPlan, bool) {
	drop := map[Rectangle]bool{}
	area := func(r LayoutRoom) int { return int(r.Interior.Width) * int(r.Interior.Height) }
	if thrones := plan.roomsOf(ModuleThrone); growth.ThroneMin > 0 && len(thrones) > 1 {
		// Keep a built room that holds the title's area, else the smallest.
		keep := -1
		for i, r := range thrones {
			switch {
			case area(r) < growth.ThroneMin:
			case built[r.Interior]:
				if keep < 0 || !built[thrones[keep].Interior] {
					keep = i
				}
			case keep < 0 || !built[thrones[keep].Interior] && area(r) < area(thrones[keep]):
				keep = i
			}
		}
		if keep >= 0 {
			for i, r := range thrones {
				if i != keep && (built[thrones[keep].Interior] || !built[r.Interior]) {
					drop[r.Interior] = true
				}
			}
		}
	}
	if growth.Demand.Known {
		for _, role := range gearRooms {
			if role == ModuleArmory && growth.Demand.Armory || role == ModuleWardrobe && growth.Demand.Wardrobe {
				continue
			}
			for _, r := range plan.roomsOf(role) {
				if !built[r.Interior] {
					drop[r.Interior] = true
				}
			}
		}
	}
	if growth.Demand.StorageIdle {
		for i, r := range plan.roomsOf(ModuleStorage) {
			if i > 0 && !built[r.Interior] {
				drop[r.Interior] = true
			}
		}
	}
	return dropRooms(plan, drop)
}

// RoomsOfRoles is how many planned rooms hold one of roles.
func RoomsOfRoles(plan LayoutPlan, roles []ModuleRole) int {
	n := 0
	for _, role := range roles {
		n += len(plan.roomsOf(role))
	}
	return n
}
