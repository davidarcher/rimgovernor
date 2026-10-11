package combatsim

import (
	"cmp"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The combat.geometry caps, NativeCombatGeometryTools.MaxCells, MaxHostiles
// and MaxRadius. The simulator answers under the same caps so a policy that
// leans on a truncation sees it here too.
const (
	geometryMaxCells    = 64
	geometryMaxHostiles = 16
	geometryMaxRadius   = 12
)

// Geometry answers a GeometryRequest from the sim's current world, as
// RoundsDefensePlanner.answerGeometry does from the game's: the ask's named
// cells scored against its hostiles and the cells proposed for its role. A
// read the native tool would refuse (a cell off the map, a hostile that is not
// on it, more than the caps) is an answer with no proposals and no lines.
// Everything is computed from the sim's present pawns and walls; nothing is
// kept between calls.
func (a *Adapter) Geometry(ask *policy.GeometryRequest) policy.GeometryReply {
	reply := policy.GeometryReply{Answered: true}
	if ask != nil && ask.Propose == policy.RoleRescuePath {
		return a.rescuePath(ask)
	}
	if ask == nil || len(ask.Hostiles) == 0 && (ask.Propose != "" || len(ask.Cells) == 0) {
		return reply
	}
	line := slices.Clone(ask.Line)
	if len(line) >= geometryMaxCells {
		line = line[:geometryMaxCells-1]
	}
	reply.Role = ask.Propose
	// The line and the shooters' cells are named, so they carry the game's
	// cover and lines of fire beside the proposals.
	named := slices.Clone(line)
	for _, c := range ask.Cells {
		if !slices.Contains(named, c) {
			named = append(named, c)
		}
	}
	limit := geometryMaxCells
	if ask.Propose != "" {
		limit--
	}
	if len(named) > limit {
		named = named[:limit]
	}
	if ask.Propose == "" && len(named) == 0 {
		return reply
	}
	hostiles, ok := a.hostiles(ask.Hostiles)
	if !ok {
		return reply
	}
	g := &a.sim.g
	for _, c := range named {
		if !g.inBounds(c) {
			return reply
		}
	}
	var proposals []domain.Cell
	switch ask.Propose {
	case policy.RoleCoverBehindLine:
		if proposals, ok = a.coverBehindLine(line, named, hostiles); !ok {
			return reply
		}
	case policy.RoleAdjacentToChoke:
		if !g.inBounds(ask.Choke) || !g.inBounds(ask.OurSide) || ask.Choke == ask.OurSide {
			return reply
		}
		proposals = a.adjacentToChoke(ask.Choke, ask.OurSide, named)
	case policy.RoleFiringCells:
		targets := ask.Targets
		if len(targets) > geometryMaxHostiles {
			targets = targets[:geometryMaxHostiles]
		}
		if !g.inBounds(ask.From) || len(targets) == 0 || slices.ContainsFunc(targets, func(c domain.Cell) bool { return !g.inBounds(c) }) {
			return reply
		}
		proposals = a.firingCells(ask.From, targets, named, hostiles)
	}
	if room := geometryMaxCells - len(named); len(proposals) > room {
		proposals = proposals[:room]
	}
	colonists := a.colonistCells()
	score := func(c domain.Cell) policy.ScoredCell {
		sc := policy.ScoredCell{Cell: c}
		for _, h := range hostiles {
			l := policy.CoverLine{
				Hostile: h.id, Cover: a.sim.CoverBlockChance(c, h.pos, true), HostileCover: a.sim.CoverBlockChance(h.pos, c, true),
				LineOfFire: g.lineOfSight(c, h.pos, true, 0, 0),
			}
			sc.Lines = append(sc.Lines, l)
			reply.Lines = append(reply.Lines, policy.SightLine{Cell: c, Hostile: h.id, LineOfFire: l.LineOfFire, ColonistInPath: onPath(c, h.pos, colonists)})
		}
		return sc
	}
	for _, c := range named {
		if g.standable(c) {
			reply.Standable = append(reply.Standable, c)
		}
		reply.Scored = append(reply.Scored, score(c))
	}
	for _, c := range proposals {
		reply.Proposals = append(reply.Proposals, c)
		reply.Standable = append(reply.Standable, c)
		reply.Scored = append(reply.Scored, score(c))
	}
	return reply
}

// hostiles resolves the ask's hostile ids to spawned pawns, in ask order. The
// native tool refuses an id that is not spawned (a dead pawn is not) and more
// than the cap, or a repeat.
func (a *Adapter) hostiles(ids []domain.PawnID) ([]*pawn, bool) {
	if len(ids) > geometryMaxHostiles {
		return nil, false
	}
	var out []*pawn
	for i, id := range ids {
		j := a.sim.pawnIndex(id)
		if j < 0 || a.sim.pawns[j].dead || slices.Contains(ids[:i], id) {
			return nil, false
		}
		out = append(out, &a.sim.pawns[j])
	}
	return out, true
}

// colonistCells are the cells of the colonists on the map: downed ones
// included, dead ones gone (FreeColonistsSpawned).
func (a *Adapter) colonistCells() map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for i := range a.sim.pawns {
		if p := &a.sim.pawns[i]; a.colonist(p) && !p.dead {
			out[p.pos] = true
		}
	}
	return out
}

// onPath ports the colonist-in-path test: a colonist on the line between the
// cell and the hostile, neither end counting.
func onPath(from, to domain.Cell, colonists map[domain.Cell]bool) bool {
	found := false
	pointsOnLine(from, to, func(c domain.Cell) bool {
		found = c != from && c != to && colonists[c]
		return !found
	})
	return found
}

// bestCover is NativeCombatGeometryTools.BestCover: the cell's block chance
// against the hostile that gets the most of it.
func (a *Adapter) bestCover(c domain.Cell, hostiles []*pawn) float64 {
	best := 0.0
	for _, h := range hostiles {
		best = max(best, a.sim.CoverBlockChance(c, h.pos, true))
	}
	return best
}

func distSq(a, b domain.Cell) int {
	dx, dz := int(a.X-b.X), int(a.Z-b.Z)
	return dx*dx + dz*dz
}

// candidate is the propose roles' filter: on the map, not named, standable.
func (a *Adapter) candidate(c domain.Cell, named []domain.Cell) bool {
	return a.sim.g.inBounds(c) && !slices.Contains(named, c) && a.sim.g.standable(c)
}

// coverBehindLine is the cover_behind_line role: the standable cells next to
// the line, off it, with any cover against the hostiles, best cover first,
// then nearest the line, then by (x, z).
func (a *Adapter) coverBehindLine(line, named []domain.Cell, hostiles []*pawn) ([]domain.Cell, bool) {
	if len(line) == 0 {
		return nil, false
	}
	for _, c := range line {
		if !a.sim.g.inBounds(c) {
			return nil, false
		}
	}
	type scored struct {
		cell  domain.Cell
		cover float64
		near  int
	}
	var found []scored
	seen := map[domain.Cell]bool{}
	for _, l := range line {
		for _, d := range adjacent {
			c := add(l, d)
			if seen[c] || slices.Contains(line, c) || !a.candidate(c, named) {
				continue
			}
			seen[c] = true
			if cover := a.bestCover(c, hostiles); cover > 0 {
				near := distSq(c, line[0])
				for _, o := range line {
					near = min(near, distSq(c, o))
				}
				found = append(found, scored{c, cover, near})
			}
		}
	}
	slices.SortFunc(found, func(x, y scored) int {
		return cmp.Or(cmp.Compare(y.cover, x.cover), cmp.Compare(x.near, y.near), cmp.Compare(x.cell.X, y.cell.X), cmp.Compare(x.cell.Z, y.cell.Z))
	})
	return cells(found, func(s scored) domain.Cell { return s.cell }), true
}

func cells[T any](in []T, f func(T) domain.Cell) []domain.Cell {
	out := make([]domain.Cell, len(in))
	for i, v := range in {
		out[i] = f(v)
	}
	return out
}

// adjacentToChoke is the adjacent_to_choke role: the standable cells beside
// the choke strictly nearer our side than the choke is, nearest first.
func (a *Adapter) adjacentToChoke(choke, ourSide domain.Cell, named []domain.Cell) []domain.Cell {
	limit := distSq(choke, ourSide)
	var found []domain.Cell
	for _, d := range adjacent {
		if c := add(choke, d); a.candidate(c, named) && distSq(c, ourSide) < limit {
			found = append(found, c)
		}
	}
	slices.SortFunc(found, func(x, y domain.Cell) int {
		return cmp.Or(cmp.Compare(distSq(x, ourSide), distSq(y, ourSide)), cmp.Compare(x.X, y.X), cmp.Compare(x.Z, y.Z))
	})
	return found
}

// firingCells is the firing_cells role: standable cells within the radius of
// from that see at least one target, most targets first, then best cover,
// then nearest from, then by (x, z).
func (a *Adapter) firingCells(from domain.Cell, targets, named []domain.Cell, hostiles []*pawn) []domain.Cell {
	type scored struct {
		cell  domain.Cell
		sight int
		cover float64
	}
	var found []scored
	r := int32(geometryMaxRadius)
	for dx := -r; dx <= r; dx++ {
		for dz := -r; dz <= r; dz++ {
			if dx*dx+dz*dz > r*r {
				continue
			}
			c := domain.Cell{X: from.X + dx, Z: from.Z + dz}
			if !a.candidate(c, named) {
				continue
			}
			sight := 0
			for _, t := range targets {
				if a.sim.g.lineOfSight(c, t, true, 0, 0) {
					sight++
				}
			}
			if sight > 0 {
				found = append(found, scored{c, sight, a.bestCover(c, hostiles)})
			}
		}
	}
	slices.SortFunc(found, func(x, y scored) int {
		return cmp.Or(cmp.Compare(y.sight, x.sight), cmp.Compare(y.cover, x.cover), cmp.Compare(distSq(x.cell, from), distSq(y.cell, from)),
			cmp.Compare(x.cell.X, y.cell.X), cmp.Compare(x.cell.Z, y.cell.Z))
	})
	return cells(found, func(s scored) domain.Cell { return s.cell })
}

// rescuePath answers a rescue_path ask with the walker's route to the goal,
// each cell flagged for hostile line of fire and a door (answerRescuePath).
// The game paths to touch the goal; here the route is the pawn's path to the
// goal cut at the first cell touching it (approximations.md A23).
func (a *Adapter) rescuePath(ask *policy.GeometryRequest) policy.GeometryReply {
	reply := policy.GeometryReply{Answered: true, Role: policy.RoleRescuePath}
	if len(ask.Hostiles) == 0 || ask.Pawn == "" {
		return reply
	}
	hostiles, ok := a.hostiles(ask.Hostiles)
	i := a.sim.pawnIndex(ask.Pawn)
	g := &a.sim.g
	if !ok || i < 0 || a.sim.pawns[i].dead || !g.inBounds(ask.To) {
		return reply
	}
	walker := &a.sim.pawns[i]
	var route []domain.Cell
	if ask.To != walker.pos {
		path, found := a.sim.findPath(walker, ask.To)
		if !found {
			return reply
		}
		for _, ci := range path {
			c := g.cell(int(ci))
			route = append(route, c)
			if max(abs32(c.X-ask.To.X), abs32(c.Z-ask.To.Z)) <= 1 {
				break
			}
		}
	}
	for _, c := range route[:min(len(route), geometryMaxCells)] {
		rc := policy.RouteCell{Cell: c, Door: g.thing(c).Door}
		for _, h := range hostiles {
			rc.LineOfFire = rc.LineOfFire || g.lineOfSight(c, h.pos, true, 0, 0)
		}
		reply.Route = append(reply.Route, rc)
	}
	return reply
}
