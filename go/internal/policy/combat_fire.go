package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Spacing and friendly fire (#861): firing cells a tile apart, no attack
// along a line a colonist stands on, and hold fire on a hostile locked in
// melee with one of our blockers.

// spaceCells orders candidate firing cells so the first ones keep at least
// one empty tile (8-way) between each other: a greedy spaced pick in the
// given order, then the rest in order. Formation assigns cells front to
// back, so defenders stand spaced while the candidates allow it and pack
// only past that, rather than stand idle.
func spaceCells(cells []domain.Cell) []domain.Cell {
	var spaced, rest []domain.Cell
	for _, c := range cells {
		if slices.ContainsFunc(spaced, func(s domain.Cell) bool { return adjacent8(s, c) }) {
			rest = append(rest, c)
		} else {
			spaced = append(spaced, c)
		}
	}
	return append(spaced, rest...)
}

func adjacent8(a, b domain.Cell) bool {
	dx, dz := a.X-b.X, a.Z-b.Z
	return dx >= -1 && dx <= 1 && dz >= -1 && dz <= 1
}

// SightLine is combat.geometry's answer for one (cell, hostile) pair (#851).
type SightLine struct {
	Cell           domain.Cell
	Hostile        domain.PawnID
	LineOfFire     bool
	ColonistInPath bool
}

type sightKey struct {
	cell    domain.Cell
	hostile domain.PawnID
}

// shooterCells are the known cells of the orderable, live pawns: where an
// attack order at this stop fires from, named in the stop's geometry ask
// so its answer carries their lines of fire.
func shooterCells(view CombatView) []domain.Cell {
	live := view.live()
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	var out []domain.Cell
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok && orderable[p.ID] && live[p.ID] && !slices.Contains(out, c) {
			out = append(out, c)
		}
	}
	return out
}

// lineAsk is the stop's geometry ask when its attack orders have no lines
// yet: the shooters' cells against the top-scored hostiles.
func lineAsk(view CombatView, orders []CombatOrder) *GeometryRequest {
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Kind == OrderAttack }) {
		return nil
	}
	ask := &GeometryRequest{Cells: shooterCells(view)}
	for _, h := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, h.ID)
		}
	}
	if len(ask.Cells) == 0 || len(ask.Hostiles) == 0 {
		return nil
	}
	if len(ask.Cells) > maxGeometryCells {
		ask.Cells = ask.Cells[:maxGeometryCells]
	}
	return ask
}

// maxGeometryCells is bridge.CombatGeometryMaxCells.
const maxGeometryCells = 64

// clearLines drops or retargets an attack order that cannot hit from the
// shooter's cell: its line of fire crosses a colonist (#861), a ranged
// role's line is blocked (a wall, #912), or native refused it cannot_hit
// from this cell before (#912). It retargets to the top-scored hostile with
// a clear, answered line from that cell not refused from it, else gives no
// order. An unanswered pair keeps the order. A retarget becomes the role's
// target, so the focus holds on it.
func clearLines(view CombatView, orders []CombatOrder, lines []SightLine, roles []CombatRole, m CombatMemory) ([]CombatOrder, []CombatRole) {
	sight := map[sightKey]SightLine{}
	for _, l := range lines {
		sight[sightKey{l.Cell, l.Hostile}] = l
	}
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	ranged := map[domain.PawnID]bool{}
	for _, r := range roles {
		ranged[r.Pawn] = r.Ranged
	}
	blocked := func(pawn, target domain.PawnID, from domain.Cell) bool {
		if m.refusedHit(pawn, target, from) {
			return true
		}
		l, ok := sight[sightKey{from, target}]
		return ok && (l.ColonistInPath || ranged[pawn] && !l.LineOfFire)
	}
	ranked := rankThreats(view)
	roles = slices.Clone(roles)
	var out []CombatOrder
	for _, o := range orders {
		from, known := cells[o.Pawn]
		if o.Kind != OrderAttack || !known || !blocked(o.Pawn, o.Target, from) {
			out = append(out, o)
			continue
		}
		o.Target = ""
		for _, h := range ranked {
			if l, ok := sight[sightKey{from, h.ID}]; ok && l.LineOfFire && !blocked(o.Pawn, h.ID, from) {
				o.Target = h.ID
				break
			}
		}
		if o.Target == "" {
			continue
		}
		for i := range roles {
			if roles[i].Pawn == o.Pawn {
				roles[i].Target = o.Target
			}
		}
		out = append(out, o)
	}
	return out, roles
}

// inMelee reports p fighting its target hand to hand: in the melee stance,
// or between swings (warmup, cooldown) standing next to it. A melee
// attacker reports cooldown after each swing (#903); reading that as the
// melee's end flipped hold fire every other stop.
func inMelee(p, target CombatPawnState) bool {
	if p.Stance == StanceMelee {
		return true
	}
	if p.Target == "" || !interruptsAim(p) {
		return false
	}
	a, ok := p.Cell.Value()
	b, ok2 := target.Cell.Value()
	return ok && ok2 && adjacent8(a, b)
}

// Fire modes, as the combat pawn row reports them.
const (
	FireAtWill = "fire_at_will"
	HoldFire   = "hold_fire"
)

// holdFire gives hold-fire to a gunner whose target is in melee with one
// of our blockers (a blocker or melee role), with a stop when the gunner
// is already shooting it, in place of its other orders; and fire-at-will
// back to a held gunner once that melee ends. A pawn whose fire mode is
// unknown counts as held when its last order was hold-fire.
func holdFire(view CombatView, roles []CombatRole, orders []CombatOrder, m CombatMemory) []CombatOrder {
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	blockers := map[domain.PawnID]bool{}
	for _, r := range roles {
		if r.Duty == DutyBlocker || !r.Ranged {
			blockers[r.Pawn] = true
		}
	}
	locked := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		if p.Dead || p.Downed || !inMelee(p, state[p.Target]) {
			continue
		}
		if blockers[p.Target] {
			locked[p.ID] = true // a hostile on a blocker
		}
		if blockers[p.ID] && p.Target != "" {
			locked[p.Target] = true // a blocker on a hostile
		}
	}
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	lastHold := map[domain.PawnID]bool{}
	for _, o := range m.Issued {
		lastHold[o.Pawn] = o.Kind == OrderFireMode && o.FireMode == HoldFire
	}
	for _, r := range roles {
		s := state[r.Pawn]
		if !r.Ranged || r.Duty == DutyBlocker || !orderable[r.Pawn] || s.Dead || s.Downed {
			continue
		}
		held := s.FireMode == HoldFire || s.FireMode == "" && lastHold[r.Pawn]
		mine := func(o CombatOrder) bool { return o.Pawn == r.Pawn }
		// A pawn mid-aim keeps its fire mode (#903) unless its shot is at
		// the hostile our blocker is fighting.
		if interruptsAim(s) && !locked[s.Target] {
			continue
		}
		switch {
		case locked[r.Target] || locked[s.Target]:
			orders = slices.DeleteFunc(orders, mine)
			if locked[s.Target] && s.Stance != StanceIdle {
				orders = append(orders, CombatOrder{Pawn: r.Pawn, Kind: OrderStop, Reason: ReasonHoldFire})
			}
			if !held {
				orders = append(orders, CombatOrder{Pawn: r.Pawn, Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonHoldFire})
			}
		case held:
			orders = slices.DeleteFunc(orders, mine)
			orders = append(orders, CombatOrder{Pawn: r.Pawn, Kind: OrderFireMode, FireMode: FireAtWill, Reason: ReasonHoldFire})
		}
	}
	return orders
}
