package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DutyTank stands in front of a static gunner (#866).
const DutyTank CombatDuty = "tank"

// StopShieldBroken is the #849 stop for a colonist's broken shield.
const StopShieldBroken CombatStopKind = "shield_broken"

// splitTanks separates the eligible defenders wearing a charged shield
// (the tanks, best armored first) from the rest. A shield belt blocks its
// wearer's own shots, so a tank is never a gunner, whatever it holds.
func splitTanks(view CombatView) (rest, tanks []SquadDefenderFacts) {
	charged := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		if s, ok := p.Shield.Value(); ok && s > 0 {
			charged[p.ID] = true
		}
	}
	for _, d := range view.Defenders {
		if charged[d.ID] && squadDefenderEligible(d) {
			tanks = append(tanks, d)
		} else {
			rest = append(rest, d)
		}
	}
	return rest, byArmor(tanks)
}

// tankThreat reports a fight a tank is worth placing in (#866, #1153):
// the live hostiles are mostly ranged (a belt stops bullets, not blades)
// and none carries an EMP weapon, which breaks the belt at once.
func tankThreat(view CombatView) bool {
	live, ranged := 0, 0
	for _, t := range view.Threats {
		if dead, _ := t.Dead.Value(); dead || t.Building {
			continue
		}
		if downed, _ := t.Downed.Value(); downed {
			continue
		}
		live++
		if r, _ := t.RangedEquipped.Value(); r {
			ranged++
		}
	}
	emp := slices.ContainsFunc(rankThreats(view), func(h CombatPawnState) bool { return h.WeaponFacts.EMP })
	return ranged*2 > live && !emp
}

// tankCells are the cells a tank may take ahead of a gunner at g, nearest
// first: the front cell, then the cells one and two steps toward the
// approach within one cell sideways (#1153).
func tankCells(g, v domain.Cell) []domain.Cell {
	side := domain.Cell{X: v.Z, Z: v.X}
	var out []domain.Cell
	for _, step := range [][2]int32{{1, 0}, {1, -1}, {1, 1}, {2, 0}, {2, -1}, {2, 1}} {
		out = append(out, domain.Cell{X: g.X - step[0]*v.X + step[1]*side.X, Z: g.Z - step[0]*v.Z + step[1]*side.Z})
	}
	return out
}

// tankRoles puts one tank ahead of each gunner, toward the approach, in
// the gunners' line order. The tank takes the cell in front of the gunner
// when the game reported it standable (#881); when that cell is cover
// instead, the nearest standable cell ahead of the gunner, one with cover
// toward the approach first, else open ground (#1153). Gunners stay put.
// A gunner with no free cell ahead gets no tank. The cell behind the
// gunner is the tank's Home when standable. Tanks beyond the gunners, and
// every tank outside a tankThreat fight, get no role.
func tankRoles(view CombatView, tanks []SquadDefenderFacts, gunners []DefensivePosition, toward domain.Rotation, geometry GeometryReply, taken []domain.Cell) []CombatRole {
	v, ok := towardVector(toward)
	if !ok || !tankThreat(view) {
		return nil
	}
	covered := map[domain.Cell]bool{}
	for _, s := range geometry.Scored {
		covered[s.Cell] = slices.ContainsFunc(s.Lines, func(l CoverLine) bool { return l.Cover > 0 })
	}
	used := slices.Clone(taken)
	for _, g := range gunners {
		used = append(used, g.Cell)
	}
	var roles []CombatRole
	for _, g := range gunners {
		if len(roles) == len(tanks) {
			break
		}
		ahead := tankCells(g.Cell, v)
		free := slices.DeleteFunc(slices.Clone(ahead), func(c domain.Cell) bool { return !geometry.stands(c) || slices.Contains(used, c) })
		if len(free) == 0 {
			continue
		}
		cell := free[0]
		if cell != ahead[0] {
			// The front cell is taken or cover: covered ground beats open.
			if i := slices.IndexFunc(free, func(c domain.Cell) bool { return covered[c] }); i >= 0 {
				cell = free[i]
			}
		}
		used = append(used, cell)
		role := CombatRole{Pawn: tanks[len(roles)].ID, Cell: &cell, Duty: DutyTank}
		if back := (domain.Cell{X: g.Cell.X + v.X, Z: g.Cell.Z + v.Z}); geometry.stands(back) {
			role.Home = &back
		}
		roles = append(roles, role)
	}
	return roles
}

// pullBackTank is the reaction table's shield row (#866): on a shield
// broken stop for a tank, the tank leaves the tank duty and retreats to
// its Home behind its gunner, or holds where it stands without one.
func pullBackTank(stop StopEvent, m *CombatMemory) {
	if stop.Kind != StopShieldBroken {
		return
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if r.Pawn == stop.Pawn && r.Duty == DutyTank {
			r.Cell, r.Duty, r.Retreat = r.Home, "", true
		}
	}
}
