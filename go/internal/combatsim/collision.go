package combatsim

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// cellPawns lists the pawns on each cell, in the order they entered it (the
// game's per-cell thing list). Pawns share cells (approximations.md A35):
// head holds a pawn index + 1 per cell, pawn.here chains the rest.
type cellPawns struct{ head []int32 }

// enter appends pawn i to the cell's list.
func (s *Sim) enter(ci, i int) {
	s.pawns[i].here = 0
	k := &s.cells.head[ci]
	for *k != 0 {
		k = &s.pawns[*k-1].here
	}
	*k = int32(i + 1)
}

// leave removes pawn i from the cell's list; absent is a no-op.
func (s *Sim) leave(ci, i int) {
	for k := &s.cells.head[ci]; *k != 0; k = &s.pawns[*k-1].here {
		if int(*k)-1 == i {
			*k = s.pawns[i].here
			s.pawns[i].here = 0
			return
		}
	}
}

// Pawn collision, from the pinned assembly (approximations.md A35). The pather
// holds a pawn back only when PawnUtility.PawnBlockedBy says the pawn on its
// next cell blocks it: both must pass ShouldCollideWithPawns (up, alive, with
// a hostile near: mindState.anyCloseHostilesRecently), the sizes must not let
// them share, and the other must be hostile (a friendly blocks only when both
// jobs set collideWithPawns, which a walking pawn's Goto never does).
const (
	// pathBlockedCost is PathFinderJob's Cost_Blocked: a cell holding a
	// blocking pawn costs 175 more to path through.
	pathBlockedCost = 175
	// blockedNodesLeft is Pawn_PathFollower's 15 remaining nodes under which
	// a pawn blocked by the same hostile twice gives up its path.
	blockedNodesLeft = 15
	// blockedGiveUpTicks is AttackBlockingHostilePawnAfterTicks.
	blockedGiveUpTicks = 180
	// repathInterval is CheckNeedNewPathIntervalTicks.
	repathInterval = 30
	// pathBlockerRange is PathGridDoorsBlockedJob's 32 cells of Manhattan
	// distance from the path's ends inside which a pawn can block a path.
	pathBlockerRange = 32
)

// blockerAt returns the index of the pawn on cell ci that blocks p from
// entering it, -1 when none does. Hostility is the sides differing; with both
// pawns alive and up, "a hostile is near" holds for each by the other.
func (s *Sim) blockerAt(p *pawn, ci int) int {
	k := s.cells.head[ci]
	if k == 0 || p.dead || p.downed {
		return -1
	}
	for ; k != 0; k = s.pawns[k-1].here {
		o := &s.pawns[k-1]
		if o.side != p.side && o.has && !o.dead && !o.downed && !sharesBySize(o, p) {
			return int(k) - 1
		}
	}
	return -1
}

// sharesBySize ports PawnUtility.PawnsCanShareCellBecauseOfBodySize: two
// pawns under 1.5 body size whose sizes differ by more than 3.57x never
// block each other. Unknown sizes share nothing.
func sharesBySize(a, b *pawn) bool {
	x, y := a.bodySize, b.bodySize
	if !(x > 0 && y > 0) || x >= 1.5 || y >= 1.5 {
		return false
	}
	r := x / y
	if r < 1 {
		r = 1 / r
	}
	return r > 3.57
}

// blocked is a tick of Pawn_PathFollower.PatherTick facing a blocker b. Past
// 180 ticks without moving, or when the same blocker held it the tick before
// and fewer than 15 nodes remain, the pawn attacks the hostile or ends its
// job; the sim has no melee, so it ends the move. Otherwise NeedNewPath
// (every 30 ticks) re-paths from the pawn's cell, now pricing the blocker.
func (s *Sim) blocked(p *pawn, self, b int) {
	left := len(p.path) - p.pathAt
	if s.tick-p.lastMoved >= blockedGiveUpTicks || (int(p.lastBlocker) == b+1 && left < blockedNodesLeft) {
		p.halt()
		s.emit(Event{Kind: EventStopped, Pawn: p.id, Cell: p.pos})
		return
	}
	p.lastBlocker = int32(b + 1)
	if (s.tick+self)%repathInterval == 0 {
		if path, ok := s.findPath(p, p.dest); ok {
			p.path = append(p.path[:0], path...)
			p.pathAt = 0
			p.costLeft = 0
			p.lastMoved = s.tick
			s.setupNext(p)
		}
	}
}

// pathPenalty is the pathfinder's extra cost for entering cell ci with a
// blocking pawn on it, when p is itself one that collides. A pawn only counts
// within 32 cells (Manhattan) of the path's start or destination.
func (s *Sim) pathPenalty(p *pawn, ci int, to domain.Cell) float64 {
	if s.cells.head[ci] == 0 || s.blockerAt(p, ci) < 0 {
		return 0
	}
	c := s.g.cell(ci)
	if min(manhattan(c, p.pos), manhattan(c, to)) > pathBlockerRange {
		return 0
	}
	return pathBlockedCost
}
