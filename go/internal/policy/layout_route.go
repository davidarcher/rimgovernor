package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Route checks and traffic. Pawns walk the planned core: spine
// hallways, room floors and doors; walls block. The entrances are the plan's
// Entrances. Every trip pair gets a shortest path, and a path through a
// private or clean room that is not one of its ends rejects the layout.

// routeTrips are the room pairs pawns travel between; PlannedRole("") is
// the entrance.
// weight is the trip's frequency, the affinity between the two ends:
// plan scoring weights each walking distance by it.
var routeTrips = []routeTrip{
	{PlannedBedroom, PlannedDining, 3},
	{PlannedSuite, PlannedDining, 3},
	{PlannedKitchen, PlannedFreezer, 3},
	{"", PlannedFreezer, 2},
	{PlannedStorage, PlannedWorkshop, 2},
	{"", PlannedStorage, 1},
	{PlannedHospital, "", 1},
}

// routeTrip is one weighted edge of the affinity graph.
type routeTrip struct {
	from, to PlannedRole
	weight   int
}

// noThroughfare are the roles nobody walks through in a plan the old generator
// made; a room the tiler made carries its own RoomCrossing instead.
var noThroughfare = map[PlannedRole]bool{
	PlannedBedroom: true, PlannedSuite: true, PlannedShelter: true, PlannedPrison: true,
	PlannedKitchen: true, PlannedHospital: true, PlannedLab: true, PlannedThrone: true,
}

// RoomCrossing is what the game charges for walking through a room, read from
// the station group that became it. No role is looked up: the three
// mechanisms are facts about the stations inside.
//
//   - Cold: the room is kept cold (a freezer or any cooled room). Every door
//     opening equalizes its temperature with the next room (Building_Door:
//     every 34 ticks while open against every 375 closed, after a 110 tick
//     close delay), so walking through leaks the cold. Never crossed.
//   - Beds: player-faction beds (colonists, patients, babies; prisoners' and
//     animals' beds do not count). Clamor within 7 cells gives a sleeper the
//     SleepDisturbed memory (-1 mood, a day, stacking to 3); it does not wake
//     them.
//   - Clean: a station's effect scales with the room's cleanliness (the
//     catalog's NeedRoomStat Cleanliness: a cook's food poisoning chance, a
//     tending or operating bed's infection and surgery chances, research
//     speed), and pawns track filth in.
type RoomCrossing struct {
	Cold  bool
	Beds  int
	Clean bool
}

// Mechanisms is how many of the crossing costs walking through the room
// triggers; the cold mechanism is not priced but forbids (Crossable).
func (c RoomCrossing) Mechanisms() int {
	n := 0
	if c.Beds > 0 {
		n++
	}
	if c.Clean {
		n++
	}
	return n
}

// Crossable reports that crossing is a cost to price, not a ban: only a cold
// room is never crossed.
func (c RoomCrossing) Crossable() bool { return !c.Cold }

// crossingOf is the crossing facts of a group: whether it is kept cold
// (groupCold), its player-faction beds and whether it needs cleanliness.
func crossingOf(g RoomGroup) RoomCrossing {
	c := RoomCrossing{Cold: groupCold(g), Clean: slices.Contains(g.Requires, EnvClean)}
	for _, s := range g.Stations {
		switch s.Purpose {
		case PurposeSleep, PurposeSleepMed, PurposeSleepBaby:
			c.Beds += s.Count
		}
	}
	return c
}

// MayBeThrough reports that the plan may mark room r Through: it has crossing
// facts, is not cold and crossing it costs something.
func (r PlannedRoom) MayBeThrough() bool {
	return r.Crossing != nil && r.Crossing.Crossable() && r.Crossing.Mechanisms() > 0
}

// walkedThrough reports that pawns may cross room r on the way between two
// others. A room with crossing facts is walked freely when crossing triggers
// nothing, when the plan marks it Through if crossing costs, and never when
// cold; one without keeps its role's noThroughfare rule.
func walkedThrough(r PlannedRoom) bool {
	if r.Crossing == nil {
		return !noThroughfare[r.Role]
	}
	if !r.Crossing.Crossable() {
		return false
	}
	return r.Crossing.Mechanisms() == 0 || r.Through
}

// spineEntrances are the cells a plan may be entered from at worst: the
// end slabs of every spine segment (a wing corridor is a dead end).
func spineEntrances(spine []SpineSegment) []domain.Cell {
	var out []domain.Cell
	for _, s := range spine {
		lo, hi := s.From, s.To
		if lo.X > hi.X || lo.Z > hi.Z {
			lo, hi = hi, lo
		}
		alongX := lo.Z == hi.Z
		for x := lo.X; x <= hi.X; x++ {
			for z := lo.Z; z <= hi.Z; z++ {
				if alongX && x != lo.X && x != hi.X || !alongX && z != lo.Z && z != hi.Z {
					continue
				}
				for d := -SpineWidth / 2; d <= SpineWidth/2; d++ {
					if alongX {
						out = append(out, domain.Cell{X: x, Z: z + d})
					} else {
						out = append(out, domain.Cell{X: x + d, Z: z})
					}
				}
			}
		}
	}
	return out
}

// ThroughfareError is a trip whose shortest path crosses a room nobody may
// walk through; Room is that room's id.
type ThroughfareError struct {
	From, To, Crossed PlannedRole
	Room              int32
	Cell              domain.Cell
}

func (e *ThroughfareError) Error() string {
	return fmt.Sprintf("%s -> %s crosses %s at %v", e.From, e.To, e.Crossed, e.Cell)
}

// CheckRoutes paths every trip over p and returns how many paths cross each
// cell (for the overlay). A thoroughfare or an unreachable trip is an error.
// Trips whose rooms the plan lacks are skipped.
func CheckRoutes(p LayoutPlan) (map[domain.Cell]int, error) {
	walkMap, rooms := routeWalk(p)
	walk := newRouteGrid(walkMap)
	cells := tripCells(p, rooms)
	traffic := map[domain.Cell]int{}
	for _, trip := range routeTrips {
		tos, froms := cells(trip.to), cells(trip.from)
		if len(p.Entrances) == 0 && (trip.from == "" && len(tos) > 0 || trip.to == "" && len(froms) > 0) {
			return nil, fmt.Errorf("no entrances for trip %s -> %s", trip.from, trip.to)
		}
		if len(tos) == 0 {
			continue
		}
		// Every room of the target role is a goal: a ring offers pawns the
		// nearest one.
		var to []domain.Cell
		for _, g := range tos {
			to = append(to, g...)
		}
		for _, from := range froms {
			path := walk.routePath(from, to)
			if path == nil {
				return nil, fmt.Errorf("no route %s -> %s", trip.from, trip.to)
			}
			ends := map[int]bool{walk.at(path[0]): true, walk.at(path[len(path)-1]): true}
			for _, c := range path {
				traffic[c]++
				if i := walk.at(c); i >= 0 && !ends[i] && !walkedThrough(rooms[i]) {
					return nil, &ThroughfareError{From: trip.from, To: trip.to, Crossed: rooms[i].Role, Room: rooms[i].ID, Cell: c}
				}
			}
		}
	}
	if err := walk.checkReachable(p.Entrances, rooms); err != nil {
		return nil, err
	}
	return traffic, nil
}

// checkReachable demands that pawns can walk from an entrance into every room
// of a tiled plan (the rooms with crossing facts) without crossing a room
// nobody may walk through (walkedThrough): the trips cover only some roles, a
// room no trip ends in could be sealed or behind a closed room. The old
// generator's rooms are not demanded: its shelter stands apart on open ground
// the walk graph does not model. A room reached only across a closed room is a ThroughfareError
// naming the first such room on the shortest way in.
func (g *routeGrid) checkReachable(entrances []domain.Cell, rooms []PlannedRoom) error {
	tiled := 0
	for _, r := range rooms {
		if r.Crossing != nil {
			tiled++
		}
	}
	if tiled == 0 {
		return nil
	}
	if len(entrances) == 0 {
		return fmt.Errorf("no entrances to reach %d rooms", tiled)
	}
	closed := make([]bool, len(rooms))
	for i, r := range rooms {
		closed[i] = !walkedThrough(r)
	}
	reached := g.flood(entrances, closed, len(rooms))
	for i, r := range rooms {
		if reached[i] || r.Crossing == nil {
			continue
		}
		var to []domain.Cell
		in := r.Region.Bounds()
		for x := in.X; x < in.X+in.Width; x++ {
			for z := in.Z; z < in.Z+in.Height; z++ {
				if c := (domain.Cell{X: x, Z: z}); g.in(c) && g.at(c) == i {
					to = append(to, c)
				}
			}
		}
		path := g.routePath(entrances, to)
		if path == nil {
			return fmt.Errorf("room %d (%s) is not reachable from the entrances", r.ID, r.Role)
		}
		for _, c := range path {
			if j := g.at(c); j >= 0 && j != i && closed[j] {
				return &ThroughfareError{To: r.Role, Crossed: rooms[j].Role, Room: rooms[j].ID, Cell: c}
			}
		}
		return fmt.Errorf("room %d (%s) is not reachable from the entrances", r.ID, r.Role)
	}
	return nil
}

// flood marks the rooms (by index) pawns reach from the start cells when a
// closed room can be entered but not left for another room.
func (g *routeGrid) flood(start []domain.Cell, closed []bool, rooms int) []bool {
	reached := make([]bool, rooms)
	g.stamp++
	queue := g.queue[:0]
	defer func() { g.queue = queue[:0] }()
	for _, c := range start {
		if g.in(c) && g.room[g.index(c)] != 0 && g.seen[g.index(c)] != g.stamp {
			g.seen[g.index(c)] = g.stamp
			queue = append(queue, int32(g.index(c)))
		}
	}
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		here := int(g.room[i]) - 2
		if here >= 0 {
			reached[here] = true
		}
		c := g.cell(i)
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
			if !g.in(n) {
				continue
			}
			ni := g.index(n)
			if g.room[ni] == 0 || g.seen[ni] == g.stamp {
				continue
			}
			if here >= 0 && closed[here] && int(g.room[ni])-2 != here {
				continue
			}
			g.seen[ni] = g.stamp
			queue = append(queue, int32(ni))
		}
	}
	return reached
}

// tripCells returns a role's trip end cells: the entrances for "", else one
// single-cell group (the interior centre) per room of that role.
func tripCells(p LayoutPlan, rooms []PlannedRoom) func(PlannedRole) [][]domain.Cell {
	return func(role PlannedRole) [][]domain.Cell {
		if role == "" {
			if len(p.Entrances) == 0 {
				return nil
			}
			return [][]domain.Cell{p.Entrances}
		}
		var out [][]domain.Cell
		for _, r := range rooms {
			if r.Role == role {
				out = append(out, []domain.Cell{{X: r.Region.Bounds().X + r.Region.Bounds().Width/2, Z: r.Region.Bounds().Z + r.Region.Bounds().Height/2}})
			}
		}
		return out
	}
}

// routeWalk is the cells pawns walk over p, each mapped to its room index
// (in p.AllRooms()), -1 for a hallway or door cell, with those rooms.
func routeWalk(p LayoutPlan) (map[domain.Cell]int, []PlannedRoom) {
	walk := map[domain.Cell]int{}
	rooms := p.AllRooms()
	for _, s := range p.Hallways() {
		lo, hi := s.From, s.To
		if lo.X > hi.X || lo.Z > hi.Z {
			lo, hi = hi, lo
		}
		alongX := lo.Z == hi.Z
		for x := lo.X; x <= hi.X; x++ {
			for z := lo.Z; z <= hi.Z; z++ {
				for d := -SpineWidth / 2; d <= SpineWidth/2; d++ {
					c := domain.Cell{X: x, Z: z + d}
					if !alongX {
						c = domain.Cell{X: x + d, Z: z}
					}
					walk[c] = -1
				}
			}
		}
	}
	for i, r := range rooms {
		in := r.Region.Bounds()
		for x := in.X; x < in.X+in.Width; x++ {
			for z := in.Z; z < in.Z+in.Height; z++ {
				walk[domain.Cell{X: x, Z: z}] = i
			}
		}
	}
	for _, r := range rooms {
		if r.Unfenced() {
			continue // no door
		}
		if _, ok := walk[r.Door]; !ok {
			walk[r.Door] = -1
		}
		if r.Airlock != nil {
			walk[r.Airlock.Vestibule], walk[r.Airlock.Outer] = -1, -1
		}
		for _, d := range r.Doors {
			if _, ok := walk[d.Cell]; !ok {
				walk[d.Cell] = -1
			}
		}
		if r.Link != nil {
			walk[*r.Link] = -1
		}
	}
	for _, l := range p.Links {
		walk[l.Cell] = -1
	}
	for _, c := range p.Entrances {
		if _, ok := walk[c]; !ok {
			walk[c] = -1
		}
	}
	if len(p.Hallways()) == 0 && len(p.Entrances) > 0 {
		// A plan without hallways is walked over the open ground its entrances
		// stand on: every cell around the rooms that no room or wall holds. The
		// terrain is not read (the embedding sited the rooms; the entrances
		// lie in the map's largest free area).
		if fp, ok := p.CoreBounds(); ok {
			walled := map[domain.Cell]bool{}
			for _, r := range rooms {
				for _, c := range rectCells(roomWalls(r)) {
					walled[c] = true
				}
			}
			for _, c := range rectCells(pad(fp, 1)) {
				if _, taken := walk[c]; !taken && !walled[c] {
					walk[c] = -1
				}
			}
		}
	}
	return walk, rooms
}

// routeGrid is walk over its bounding box, so a path search indexes arrays
// instead of hashing cells. Search state is stamped per search, so one grid
// serves every trip without clearing.
type routeGrid struct {
	minX, minZ, w, h int32
	room             []int32 // room index + 2; 0 = not walkable
	seen, goal       []uint32
	prev             []int32
	stamp            uint32
	queue            []int32
}

func newRouteGrid(walk map[domain.Cell]int) *routeGrid {
	g := &routeGrid{}
	first := true
	var maxX, maxZ int32
	for c := range walk {
		if first {
			g.minX, maxX, g.minZ, maxZ, first = c.X, c.X, c.Z, c.Z, false
			continue
		}
		g.minX, maxX = min(g.minX, c.X), max(maxX, c.X)
		g.minZ, maxZ = min(g.minZ, c.Z), max(maxZ, c.Z)
	}
	if first {
		return g
	}
	g.w, g.h = maxX-g.minX+1, maxZ-g.minZ+1
	n := int(g.w) * int(g.h)
	g.room, g.seen, g.goal, g.prev = make([]int32, n), make([]uint32, n), make([]uint32, n), make([]int32, n)
	for c, i := range walk {
		g.room[g.index(c)] = int32(i) + 2
	}
	return g
}

func (g *routeGrid) in(c domain.Cell) bool {
	return c.X >= g.minX && c.Z >= g.minZ && c.X < g.minX+g.w && c.Z < g.minZ+g.h
}

func (g *routeGrid) index(c domain.Cell) int { return int(c.Z-g.minZ)*int(g.w) + int(c.X-g.minX) }

func (g *routeGrid) cell(i int32) domain.Cell {
	return domain.Cell{X: g.minX + i%g.w, Z: g.minZ + i/g.w}
}

// at is the room index of a walkable cell (-1 hallway or door).
func (g *routeGrid) at(c domain.Cell) int { return int(g.room[g.index(c)]) - 2 }

// routePath is a shortest 4-neighbour path over the grid from any of from to
// any of to, or nil.
func (g *routeGrid) routePath(from, to []domain.Cell) []domain.Cell {
	g.stamp++
	for _, c := range to {
		if g.in(c) {
			g.goal[g.index(c)] = g.stamp
		}
	}
	queue := g.queue[:0]
	for _, c := range from {
		if g.in(c) && g.room[g.index(c)] != 0 {
			i := g.index(c)
			g.seen[i], g.prev[i] = g.stamp, int32(i)
			queue = append(queue, int32(i))
		}
	}
	defer func() { g.queue = queue[:0] }()
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		if g.goal[i] == g.stamp {
			path := []domain.Cell{g.cell(i)}
			for g.prev[i] != i {
				i = g.prev[i]
				path = append(path, g.cell(i))
			}
			return path
		}
		c := g.cell(i)
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
			if !g.in(n) {
				continue
			}
			ni := g.index(n)
			if g.room[ni] == 0 || g.seen[ni] == g.stamp {
				continue
			}
			g.seen[ni], g.prev[ni] = g.stamp, i
			queue = append(queue, int32(ni))
		}
	}
	return nil
}
