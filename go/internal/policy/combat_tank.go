package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

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

// tankRoles puts one tank on the cell in front of each gunner, toward the
// approach, in the gunners' line order, skipping a gunner whose front cell
// the game did not report standable (#881). The cell behind the gunner is
// the tank's Home when standable. Tanks beyond the gunners get no role.
func tankRoles(tanks []SquadDefenderFacts, gunners []DefensivePosition, toward domain.Rotation, geometry GeometryReply) []CombatRole {
	v, ok := towardVector(toward)
	if !ok {
		return nil
	}
	var roles []CombatRole
	for _, g := range gunners {
		if len(roles) == len(tanks) {
			break
		}
		cell := domain.Cell{X: g.Cell.X - v.X, Z: g.Cell.Z - v.Z}
		if !geometry.stands(cell) {
			continue
		}
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
