package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Kiting constants (#901).
const (
	// kiteSpeedRatio is how much faster than the fastest hostile a kiter
	// must be (the wiki's 120% Moving).
	kiteSpeedRatio = 1.2
	// kiteLeadRange: a hostile this close to the kiter, in cells, starts
	// the lead even before it targets the kiter.
	kiteLeadRange = 8
)

// DutyKiter baits a slow pack and leads it past the line (#901).
const DutyKiter CombatDuty = "kiter"

// kite is the kiting step (#901): the manhunter tactic's, and a mech
// raid's under the hold or the sapper tactic (#923, combat_mech_kite.go).
// When kiteLead finds the hostiles all slow, the fastest gunner at least
// kiteSpeedRatio times as fast as the fastest of them is the kiter. It
// baits the nearest hostile from where it stands; once a hostile targets
// it or comes within kiteLeadRange, it leads: it retreats to the lead
// cell, so the chaser crosses the firing squad's line (and the flank
// turrets), and engages again from there.
func kite(view CombatView, m *CombatMemory) {
	lure, fastest, ok := kiteLead(view, *m)
	if !ok {
		m.Kiter, m.Leading = "", false
		return
	}
	ranked := rankThreats(view)
	if len(ranked) == 0 {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	i := -1
	for j, r := range m.Roles {
		if r.Pawn == m.Kiter {
			i = j
		}
	}
	if i < 0 {
		m.Kiter, m.Leading = "", false
		best := 0.0
		for j, r := range m.Roles {
			s := state[r.Pawn]
			if r.Ranged && r.Duty == "" && s.MoveSpeed >= kiteSpeedRatio*fastest && s.MoveSpeed > best {
				i, best = j, s.MoveSpeed
			}
		}
		if i < 0 {
			return
		}
		m.Kiter = m.Roles[i].Pawn
	}
	at, known := state[m.Kiter].Cell.Value()
	var chaser domain.PawnID
	near := int64(-1)
	for _, h := range ranked {
		c, ok := h.Cell.Value()
		if h.Target == m.Kiter || ok && known && distance2(c, at) <= kiteLeadRange*kiteLeadRange {
			m.Leading = true
		}
		if ok && known && (near < 0 || distance2(c, at) < near) {
			chaser, near = h.ID, distance2(c, at)
		}
	}
	if chaser == "" {
		chaser = ranked[0].ID
	}
	role := &m.Roles[i]
	role.Duty, role.Target, role.Cell, role.Retreat = DutyKiter, chaser, nil, false
	if m.Leading {
		role.Cell, role.Retreat = &lure, true
	}
}

// kiteLead is the kiting step's lead cell and the fastest hostile's
// speed, ok false when the fight is not kited. The manhunter tactic leads
// a pack of slow animals to the inner-line cell farthest behind the
// firing line.
func kiteLead(view CombatView, m CombatMemory) (domain.Cell, float64, bool) {
	if m.Tactic != TacticManhunter {
		return mechKiteLead(view, m)
	}
	lure, ok := rearmostRetreat(view)
	if !ok {
		return domain.Cell{}, 0, false
	}
	fastest := 0.0
	for _, h := range rankThreats(view) {
		if !classifyAnimal(h).Slow {
			return domain.Cell{}, 0, false
		}
		fastest = max(fastest, h.MoveSpeed)
	}
	return lure, fastest, true
}

// rearmostRetreat is the layout's inner-line cell farthest behind the
// firing line; ok false without a layout, a direction or an inner line.
func rearmostRetreat(view CombatView) (domain.Cell, bool) {
	layout, ok := view.Layout.Value()
	v, vok := towardVector(layout.Toward)
	if !ok || !vok || len(layout.Retreat) == 0 {
		return domain.Cell{}, false
	}
	lure := layout.Retreat[0]
	for _, c := range layout.Retreat[1:] {
		if c.X*v.X+c.Z*v.Z > lure.X*v.X+lure.Z*v.Z {
			lure = c
		}
	}
	return lure, true
}
