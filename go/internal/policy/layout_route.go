package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Route check and traffic (#780, A4). Pawns walk the planned core: spine
// hallways, room floors and doors; walls block. The entrances are the plan's
// Entrances. Every trip pair gets a shortest path, and a path through a
// private or clean room that is not one of its ends rejects the layout.

// routeTrips are the room pairs pawns travel between; ModuleRole("") is
// the entrance.
var routeTrips = [][2]ModuleRole{
	{ModuleBedroom, ModuleDining},
	{ModuleSuite, ModuleDining},
	{ModuleKitchen, ModuleFreezer},
	{"", ModuleFreezer},
	{ModuleStorage, ModuleWorkshop},
	{"", ModuleStorage},
	{ModuleHospital, ""},
}

// noThroughfare are the roles nobody may walk through.
var noThroughfare = map[ModuleRole]bool{
	ModuleBedroom: true, ModuleSuite: true, ModuleBarracks: true, ModulePrison: true,
	ModuleKitchen: true, ModuleHospital: true, ModuleLab: true, ModuleThrone: true,
}

// spineEntrances are the cells the old generator enters the base from: the
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

// CheckRoutes paths every trip over p and returns how many paths cross each
// cell (for the overlay). A thoroughfare or an unreachable trip is an error.
// Trips whose rooms the plan lacks are skipped.
func CheckRoutes(p LayoutPlan) (map[domain.Cell]int, error) {
	walk := map[domain.Cell]int{} // cell -> room index, -1 for hallway/door
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
		in := r.Interior
		for x := in.X; x < in.X+in.Width; x++ {
			for z := in.Z; z < in.Z+in.Height; z++ {
				walk[domain.Cell{X: x, Z: z}] = i
			}
		}
	}
	for _, r := range rooms {
		if _, ok := walk[r.Door]; !ok {
			walk[r.Door] = -1
		}
		if r.Link != nil {
			walk[*r.Link] = -1
		}
	}
	cells := func(role ModuleRole) [][]domain.Cell {
		if role == "" {
			if len(p.Entrances) == 0 {
				return nil
			}
			return [][]domain.Cell{p.Entrances}
		}
		var out [][]domain.Cell
		for _, r := range rooms {
			if r.Role == role {
				out = append(out, []domain.Cell{{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}})
			}
		}
		return out
	}
	traffic := map[domain.Cell]int{}
	for _, trip := range routeTrips {
		to, froms := cells(trip[1]), cells(trip[0])
		if len(p.Entrances) == 0 && (trip[0] == "" && len(to) > 0 || trip[1] == "" && len(froms) > 0) {
			return nil, fmt.Errorf("no entrances for trip %s -> %s", trip[0], trip[1])
		}
		if len(to) == 0 {
			continue
		}
		for _, from := range froms {
			path := routePath(walk, from, to[0])
			if path == nil {
				return nil, fmt.Errorf("no route %s -> %s", trip[0], trip[1])
			}
			ends := map[int]bool{walk[path[0]]: true, walk[path[len(path)-1]]: true}
			for _, c := range path {
				traffic[c]++
				if i := walk[c]; i >= 0 && !ends[i] && noThroughfare[rooms[i].Role] {
					return nil, fmt.Errorf("%s -> %s crosses %s at %v", trip[0], trip[1], rooms[i].Role, c)
				}
			}
		}
	}
	return traffic, nil
}

// routePath is a shortest 4-neighbour path over walk from any of from to
// any of to, or nil.
func routePath(walk map[domain.Cell]int, from, to []domain.Cell) []domain.Cell {
	goal := map[domain.Cell]bool{}
	for _, c := range to {
		goal[c] = true
	}
	prev := map[domain.Cell]domain.Cell{}
	var queue []domain.Cell
	for _, c := range from {
		if _, ok := walk[c]; ok {
			prev[c] = c
			queue = append(queue, c)
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		if goal[c] {
			path := []domain.Cell{c}
			for prev[c] != c {
				c = prev[c]
				path = append(path, c)
			}
			return path
		}
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
			if _, ok := walk[n]; !ok {
				continue
			}
			if _, seen := prev[n]; seen {
				continue
			}
			prev[n] = c
			queue = append(queue, n)
		}
	}
	return nil
}
