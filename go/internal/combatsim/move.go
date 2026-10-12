package combatsim

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	maxTicksPerMove = 450
	diagonalFactor  = 1.41421
)

// pawn is the movement state of one pawn: the port of Pawn_PathFollower's
// nextCell, nextCellCostLeft and nextCellCostTotal.
type pawn struct {
	id    domain.PawnID
	pos   domain.Cell
	speed float64 // MoveSpeed stat, cells per second
	dest  domain.Cell

	moving  bool
	urgency Urgency
	path    []int32 // cell indices still to enter, in order
	pathAt  int
	next    domain.Cell
	// costLeft counts down by one per tick; at or below zero the pawn enters
	// next and the overshoot carries into the following cell.
	costLeft    float64
	blockedSent bool
	// lastBlocker is the pawn index + 1 that blocked the previous tick's
	// entry, 0 when nothing did (Pawn_PathFollower.lastBlocker); lastMoved is
	// the last tick the pawn paid movement cost or was given a path
	// (lastMovedTick).
	lastBlocker int32
	lastMoved   int

	// here is the next pawn index + 1 on the same cell (cellPawns), 0 last.
	here int32

	side string
	combatant
}

// ticksPerMove ports Pawn.TicksPerMove for a pawn standing on from: 60 over
// MoveSpeed, divided by the weather multiplier on an unroofed cell, times
// sqrt(2) for a diagonal, clamped to [1, 450]; a zero speed costs 450.
func (s *Sim) ticksPerMove(speed float64, from domain.Cell, diagonal bool) float64 {
	v := speed / 60
	if v == 0 {
		return maxTicksPerMove
	}
	t := 1 / v
	if !s.g.roofed[s.g.idx(from)] {
		t /= s.weather
	}
	if diagonal {
		t *= diagonalFactor
	}
	return math.Min(math.Max(t, 1), maxTicksPerMove)
}

// stepCost ports Pawn_PathFollower.CostToMoveIntoCell for a pawn on from
// entering the walkable neighbour c, without the locomotion urgency (which
// scales the whole path and is applied by urgentCost).
func (s *Sim) stepCost(speed float64, from, c domain.Cell) float64 {
	diagonal := c.X != from.X && c.Z != from.Z
	cost := s.ticksPerMove(speed, from, diagonal) + float64(s.g.cellCost(c))
	return math.Min(cost, maxTicksPerMove)
}

// urgentCost applies the job's locomotion urgency and the one-tick floor.
func urgentCost(cost float64, u Urgency) float64 {
	switch u {
	case Amble:
		cost = math.Max(cost*3, 60)
	case Walk:
		cost = math.Max(cost*2, 50)
	case Sprint:
		cost = math.RoundToEven(cost * 0.75)
	}
	return math.Max(cost, 1)
}

func (s *Sim) apply(o Order) error {
	i := s.pawnIndex(o.Pawn)
	if i < 0 {
		return unknownPawn(o.Pawn)
	}
	p := &s.pawns[i]
	if p.dead || p.downed {
		return nil
	}
	switch o.Kind {
	case OrderAttack:
		return s.startAttack(i, o)
	case OrderLeave:
		s.leaveMap(i)
	case OrderStop:
		p.cancelAttack()
		if p.moving {
			p.halt()
			s.emit(Event{Kind: EventStopped, Pawn: p.id, Cell: p.pos})
		}
	case OrderMove:
		p.cancelAttack()
		s.startMove(i, o)
	}
	return nil
}

func (p *pawn) halt() {
	p.moving = false
	p.path = p.path[:0]
	p.pathAt = 0
	p.costLeft = 0
	p.blockedSent = false
	p.lastBlocker = 0
	p.dest = p.pos
}

// startMove finds a path and begins walking it. A pawn already walking to the
// same destination at the same urgency keeps its place (Pawn_PathFollower
// StartPath returns early).
func (s *Sim) startMove(i int, o Order) {
	p := &s.pawns[i]
	if p.moving && p.dest == o.To && p.urgency == o.Urgency {
		return
	}
	p.halt()
	g := &s.g
	if !g.standable(o.To) {
		s.emit(Event{Kind: EventPathFailed, Pawn: p.id, Cell: o.To})
		return
	}
	if o.To == p.pos {
		p.dest = o.To
		s.emit(Event{Kind: EventArrived, Pawn: p.id, Cell: p.pos})
		return
	}
	path, ok := s.findPath(p, o.To)
	if !ok {
		s.emit(Event{Kind: EventPathFailed, Pawn: p.id, Cell: o.To})
		return
	}
	p.path = append(p.path[:0], path...)
	p.pathAt = 0
	p.moving = true
	p.dest = o.To
	p.urgency = o.Urgency
	p.lastMoved = s.tick
	s.setupNext(p)
	s.emit(Event{Kind: EventMoveStarted, Pawn: p.id, Cell: o.To, ETA: s.tick + s.arrivalTicks(p)})
}

// setupNext ports SetupMoveIntoNextCell: choose the next cell, price it, carry
// any overshoot, and open a closed door being approached.
func (s *Sim) setupNext(p *pawn) {
	p.next = s.g.cell(int(p.path[p.pathAt]))
	cost := urgentCost(s.stepCost(p.speed, p.pos, p.next), p.urgency)
	p.costLeft = math.Max(cost+math.Min(p.costLeft, 0), 1)
	p.blockedSent = false
	if i := s.g.idx(p.next); s.g.things[s.g.thingI[i]].Door && !s.g.doorOpen[i] {
		s.g.doorOpen[i] = true
		s.emit(Event{Kind: EventDoorOpened, Pawn: p.id, Cell: p.next})
	}
}

// arrivalTicks dry-runs the pathing recurrence for a just-started pawn: the
// ticks until it enters its destination if nothing blocks it.
func (s *Sim) arrivalTicks(p *pawn) int {
	left := 0.0
	from := p.pos
	ticks := 0
	for _, ci := range p.path[p.pathAt:] {
		c := s.g.cell(int(ci))
		cost := urgentCost(s.stepCost(p.speed, from, c), p.urgency)
		left = math.Max(cost+math.Min(left, 0), 1)
		for left > 0 {
			left--
			ticks++
		}
		from = c
	}
	return ticks
}

// tick advances one pawn one game tick (PatherTick): pay a tick of the next
// cell's cost, and enter the cell when it is paid. A hostile on the next cell
// holds the pawn back before any cost is paid (collision.go).
func (p *pawn) tick(s *Sim, self int) {
	if !p.moving || p.dead || p.downed || p.stance != stanceIdle {
		return
	}
	g := &s.g
	ni := g.idx(p.next)
	// WillCollideWithPawnAt is checked before any cost is paid: a pawn facing
	// a blocker stands still.
	if b := s.blockerAt(p, ni); b >= 0 {
		if !p.blockedSent {
			p.blockedSent = true
			s.emit(Event{Kind: EventBlocked, Pawn: p.id, Cell: p.next})
		}
		s.blocked(p, self, b)
		return
	}
	p.lastBlocker = 0
	p.lastMoved = s.tick
	if p.costLeft > 0 {
		p.costLeft--
	}
	if p.costLeft > 0 {
		return
	}
	s.leave(g.idx(p.pos), self)
	s.enter(ni, self)
	p.pos = p.next
	p.pathAt++
	s.emit(Event{Kind: EventMoved, Pawn: p.id, Cell: p.pos})
	if p.pathAt >= len(p.path) {
		p.moving = false
		p.costLeft = 0
		s.emit(Event{Kind: EventArrived, Pawn: p.id, Cell: p.pos})
		return
	}
	s.setupNext(p)
}

// leaveMap takes pawn i off the map: it frees its cell, stops any attack and
// movement, ends other pawns' attacks on it and stops it being a combatant.
// Metrics still counts it, as a hostile that was neither downed nor killed.
func (s *Sim) leaveMap(i int) {
	p := &s.pawns[i]
	if !p.has || p.dead {
		return
	}
	p.halt()
	p.cancelAttack()
	p.stance, p.burstLeft = stanceIdle, 0
	s.leave(s.g.idx(p.pos), i)
	p.has, p.left = false, true
	for j := range s.pawns {
		if o := &s.pawns[j]; o.has && o.target == i {
			s.emit(Event{Kind: EventAttackEnded, Pawn: o.id, Target: p.id})
			o.target, o.once = -1, false
		}
	}
	s.emit(Event{Kind: EventLeftMap, Pawn: p.id, Cell: p.pos})
}
