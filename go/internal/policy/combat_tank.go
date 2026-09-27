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
// approach, in the gunners' line order. Tanks beyond the gunners get no
// role.
func tankRoles(tanks []SquadDefenderFacts, gunners []DefensivePosition, toward domain.Rotation) []CombatRole {
	v, ok := towardVector(toward)
	if !ok {
		return nil
	}
	var roles []CombatRole
	for i := range min(len(tanks), len(gunners)) {
		g := gunners[i].Cell
		cell := domain.Cell{X: g.X - v.X, Z: g.Z - v.Z}
		if cell.X < 0 || cell.Z < 0 {
			continue
		}
		roles = append(roles, CombatRole{Pawn: tanks[i].ID, Cell: &cell, Duty: DutyTank})
	}
	return roles
}

// pullBackTank is the reaction table's shield row (#866): on a shield
// broken stop for a tank, the tank retreats to the cell behind its gunner
// and leaves the tank duty.
func pullBackTank(view CombatView, stop StopEvent, m *CombatMemory) {
	layout, ok := view.Layout.Value()
	v, vok := towardVector(layout.Toward)
	if stop.Kind != StopShieldBroken || !ok || !vok {
		return
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if r.Pawn != stop.Pawn || r.Duty != DutyTank || r.Cell == nil {
			continue
		}
		// The tank's cell is one step in front of its gunner; behind the
		// gunner is two steps back from it.
		back := domain.Cell{X: r.Cell.X + 2*v.X, Z: r.Cell.Z + 2*v.Z}
		r.Cell, r.Duty, r.Retreat = &back, "", true
	}
}
