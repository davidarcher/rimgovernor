package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CombatDuty is a defender's special duty in a formation, beyond a firing
// cell and a target.
type CombatDuty string

const (
	// DutyBlocker holds a cell just outside the choke (#864).
	DutyBlocker CombatDuty = "blocker"
	// DutyReserve waits to relieve a hurt blocker (#864).
	DutyReserve CombatDuty = "reserve"
)

// maxChokeBlockers is how many brawlers block the choke (#845: up to 3).
const maxChokeBlockers = 3

// chokeAnchorSteps is how far along the corridor direction the
// adjacent_to_choke ask's our_side anchor lies: far enough that only the
// choke's neighbours on our side are strictly nearer to it.
const chokeAnchorSteps = 3

// blockingChoke says whether Formation runs the melee blocking formation
// (#864): a layout with a known choke and at least one eligible brawler.
// It returns the choke and the our_side anchor for the adjacent_to_choke
// ask.
func blockingChoke(view CombatView) (domain.Cell, domain.Cell, bool) {
	layout, ok := view.Layout.Value()
	if !ok {
		return domain.Cell{}, domain.Cell{}, false
	}
	choke, ok := layout.Choke.Value()
	v, vok := towardVector(layout.Toward)
	if !ok || !vok || len(brawlers(view.Defenders)) == 0 {
		return domain.Cell{}, domain.Cell{}, false
	}
	side := domain.Cell{X: choke.X + chokeAnchorSteps*v.X, Z: choke.Z + chokeAnchorSteps*v.Z}
	if side.X < 0 || side.Z < 0 {
		return domain.Cell{}, domain.Cell{}, false
	}
	return choke, side, true
}

// brawlers are the eligible melee-only defenders, best armored first
// (unknown armor last), then by id.
func brawlers(defenders []SquadDefenderFacts) []SquadDefenderFacts {
	var out []SquadDefenderFacts
	for _, d := range defenders {
		if squadDefenderEligible(d) && positive(d.MeleeEquipped) && !positive(d.RangedEquipped) {
			out = append(out, d)
		}
	}
	sort.SliceStable(out, func(i, j int) bool {
		ai, ik := out[i].Armor.Value()
		aj, jk := out[j].Armor.Value()
		if ik != jk {
			return ik
		}
		if ik && ai != aj {
			return ai > aj
		}
		return out[i].ID < out[j].ID
	})
	return out
}

// blockingRoles puts the best-armored brawlers on the game's proposed
// cells just outside the choke, at most three, and keeps the next brawler
// as the reserve (on the next proposed cell when there is one). With two
// or more brawlers one is always held back, so a hurt blocker can be
// relieved.
func blockingRoles(defenders []SquadDefenderFacts, proposals []domain.Cell) []CombatRole {
	pool := brawlers(defenders)
	n := min(maxChokeBlockers, len(proposals), len(pool))
	if n == len(pool) && n >= 2 {
		n--
	}
	var roles []CombatRole
	for i := range n {
		cell := proposals[i]
		roles = append(roles, CombatRole{Pawn: pool[i].ID, Cell: &cell, Duty: DutyBlocker})
	}
	if n > 0 && n < len(pool) {
		role := CombatRole{Pawn: pool[n].ID, Duty: DutyReserve}
		if n < len(proposals) {
			cell := proposals[n]
			role.Cell = &cell
		}
		roles = append(roles, role)
	}
	return roles
}

// relieveBlocker is the reaction table's relief row (#864): on a serious
// injury to a blocker, the reserve takes the blocker's cell and the hurt
// blocker pulls back to where the reserve stands. Without a live reserve
// whose place is known nothing changes, and the fall-back row (#860) takes
// the stop. It reports whether it swapped.
func relieveBlocker(view CombatView, stop StopEvent, m *CombatMemory) bool {
	if stop.Kind != StopSeriousInjury {
		return false
	}
	hurt, reserve := -1, -1
	for i, r := range m.Roles {
		switch {
		case r.Pawn == stop.Pawn && r.Duty == DutyBlocker && r.Cell != nil:
			hurt = i
		case r.Duty == DutyReserve && reserve < 0:
			reserve = i
		}
	}
	if hurt < 0 || reserve < 0 {
		return false
	}
	back := m.Roles[reserve].Cell
	for _, p := range view.Pawns {
		if at, ok := p.Cell.Value(); ok && p.ID == m.Roles[reserve].Pawn {
			back = &at
		}
	}
	if back == nil {
		return false
	}
	m.Roles[reserve].Cell, m.Roles[reserve].Duty = m.Roles[hurt].Cell, DutyBlocker
	// The relieved blocker's pull-back is a retreat: it passes the aim
	// guard and the #860 fall-back leaves it in place.
	m.Roles[hurt].Cell, m.Roles[hurt].Duty, m.Roles[hurt].Target, m.Roles[hurt].Retreat = back, "", "", true
	return true
}
