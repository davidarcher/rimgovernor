package policy

import (
	"fmt"

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

// noThroughfare are the roles nobody walks through by default.
var noThroughfare = map[PlannedRole]bool{
	PlannedBedroom: true, PlannedSuite: true, PlannedShelter: true, PlannedPrison: true,
	PlannedKitchen: true, PlannedHospital: true, PlannedLab: true, PlannedThrone: true,
}

// passableRoles are the noThroughfare roles a plan may nevertheless let pawns
// walk through (PlannedRoom.Through) when packed rooms leave no other way in.
// The game reads the cleanliness of exactly these rooms (the bed roles' mood,
// a kitchen's food poisoning, a hospital's infection), and foot traffic only
// tracks filth in, so crossing one is a cost the walk term and the cleaners
// bear, not a rule. The rest stay closed by judgment, no game number: a prison
// and a shelter are secured or self-contained, a laboratory and a throne room
// hold work and ceremony nobody should cross.
var passableRoles = map[PlannedRole]bool{PlannedBedroom: true, PlannedSuite: true, PlannedKitchen: true, PlannedHospital: true}

// MayPassThrough reports that a room of role may be marked Through.
func MayPassThrough(role PlannedRole) bool { return passableRoles[role] }

// walkedThrough reports that pawns may cross room r on the way between two
// others: its role allows it, or the plan marks it Through.
func walkedThrough(r PlannedRoom) bool { return !noThroughfare[r.Role] || r.Through }

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
	return traffic, nil
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
