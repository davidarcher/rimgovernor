package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Kiting constants (#901).
const (
	// kiteSpeedRatio is how much faster than the fastest animal a kiter
	// must be (the wiki's 120% Moving).
	kiteSpeedRatio = 1.2
	// kiteLeadRange: an animal this close to the kiter, in cells, starts
	// the lead even before it targets the kiter.
	kiteLeadRange = 8
)

// DutyKiter baits a slow pack and leads it past the line (#901).
const DutyKiter CombatDuty = "kiter"

// manhunterKite is the manhunter tactic's kiting step (#901). With a
// complete layout with an inner line and every live animal slow, the
// fastest gunner at least kiteSpeedRatio times as fast as the fastest
// animal is the kiter. It baits the nearest animal from where it stands;
// once an animal targets it or comes within kiteLeadRange, it leads: it
// retreats to the inner-line cell farthest behind the firing line, so
// the chaser crosses the firing squad's line and the flank turrets, and
// engages again from there.
func manhunterKite(view CombatView, m *CombatMemory) {
	layout, ok := view.Layout.Value()
	v, vok := towardVector(layout.Toward)
	if m.Tactic != TacticManhunter || !ok || !vok || len(layout.Retreat) == 0 {
		m.Kiter, m.Leading = "", false
		return
	}
	ranked := rankThreats(view)
	fastest := 0.0
	for _, h := range ranked {
		if !classifyAnimal(h).Slow {
			m.Kiter, m.Leading = "", false
			return
		}
		fastest = max(fastest, h.MoveSpeed)
	}
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
		lure := layout.Retreat[0]
		for _, c := range layout.Retreat[1:] {
			if c.X*v.X+c.Z*v.Z > lure.X*v.X+lure.Z*v.Z {
				lure = c
			}
		}
		role.Cell, role.Retreat = &lure, true
	}
}
