package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DutyInterceptor goes out to shoot a digging sapper (#914).
const DutyInterceptor CombatDuty = "interceptor"

// Intercept constants (#914).
const (
	// interceptReach is the share of its weapon range an interceptor
	// stands off the digger.
	interceptReach = 0.75
	// interceptDefaultRange is the stand-off, in cells, for an unknown range.
	interceptDefaultRange = 20.0
)

// sapperIntercept is the sapper tactic's intercept step (#914). Sappers
// use no cover while they dig, so while a live humanlike sapper is
// digging (a Mine job) and our gunners are at least as many as the live
// hostiles, every gunner goes out: it moves to the cell on the line from
// its nearest digger toward itself at interceptReach of its weapon range,
// and attacks that digger. A mech breacher is never intercepted: breach
// raids are never fought in the open. Once no one digs, or we are
// outnumbered, the gunners return to the breach posts.
func sapperIntercept(view CombatView, m *CombatMemory) {
	if m.Tactic != TacticSapper {
		m.Intercept = false
		return
	}
	humanlike := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		humanlike[domain.PawnID(t.ID)] = positive(t.Humanlike)
	}
	var diggers []CombatPawnState
	for _, h := range liveSappers(view) {
		if h.Job == "Mine" && humanlike[h.ID] {
			if _, ok := h.Cell.Value(); ok {
				diggers = append(diggers, h)
			}
		}
	}
	gunners := 0
	for _, r := range m.Roles {
		if r.Ranged {
			gunners++
		}
	}
	if len(diggers) == 0 || gunners < len(rankThreats(view)) {
		if m.Intercept {
			m.Intercept = false
			if b, ok := predictBreach(view); ok {
				m.Roles = sapperFormation(view, b)
			}
		}
		return
	}
	m.Intercept = true
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if !r.Ranged {
			continue
		}
		s := state[r.Pawn]
		at, known := s.Cell.Value()
		digger := diggers[0]
		if known {
			for _, d := range diggers[1:] {
				c, _ := d.Cell.Value()
				b, _ := digger.Cell.Value()
				if distance2(c, at) < distance2(b, at) {
					digger = d
				}
			}
		}
		r.Duty, r.Target, r.Cell, r.Retreat = DutyInterceptor, digger.ID, nil, false
		if known {
			c := standOff(digger, at, s.WeaponRange)
			r.Cell = &c
		}
	}
}

// standOff is the cell on the line from the digger toward from, at
// interceptReach of reach (interceptDefaultRange when unknown), or from
// itself when it is already that close.
func standOff(digger CombatPawnState, from domain.Cell, reach float64) domain.Cell {
	want := interceptDefaultRange
	if reach > 0 {
		want = interceptReach * reach
	}
	return standOffAt(digger, from, want)
}

// standOffAt is the cell on the line from target toward from, want cells
// from target, or from itself when it is already that close.
func standOffAt(target CombatPawnState, from domain.Cell, want float64) domain.Cell {
	d, _ := target.Cell.Value()
	dx, dz := float64(from.X-d.X), float64(from.Z-d.Z)
	dist := math.Hypot(dx, dz)
	if dist <= want {
		return from
	}
	return domain.Cell{X: d.X + int32(math.Round(dx/dist*want)), Z: d.Z + int32(math.Round(dz/dist*want))}
}
