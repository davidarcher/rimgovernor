package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OrderAttackGround force-fires a pawn's ground-targetable verb at a cell
// (the combat.orders attack_ground order).
const OrderAttackGround CombatOrderKind = "attack_ground"

// ReasonRocketClump is a rocket carrier's ground shot at a clump.
const ReasonRocketClump CombatOrderReason = "rocket_clump"

// A clump is rocketClumpMin live hostiles within rocketClumpRadius cells of
// a hostile's cell: a sapper team at its wall, a siege camp. No
// colonist may stand within rocketSafeRadius of the aim.
const (
	rocketClumpMin    = 3
	rocketClumpRadius = 3.0
	rocketSafeRadius  = 5.0
)

// rocketLauncher reports a weapon spent by its shot: a rocket launcher.
func rocketLauncher(weapon WeaponDef) bool { return weapon.OneUse }

// rocketClumps gives every orderable rocket carrier a ground shot at the
// densest hostile clump in its range, nearest on a tie; a carrier
// with no clump in range keeps its role. A mortar crew or a rescuer is left
// alone.
func rocketClumps(view CombatView, m *CombatMemory) {
	for i := range m.Roles {
		m.Roles[i].Ground = nil
	}
	live := view.live()
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = !t.Building && !positive(t.Dead) && !positive(t.Downed)
	}
	colonist := map[domain.PawnID]bool{}
	for _, d := range view.Defenders {
		colonist[d.ID] = true
	}
	var foes, friends []domain.Cell
	for _, p := range view.Pawns {
		c, ok := p.Cell.Value()
		switch {
		case !ok || p.Dead || p.Downed:
		case hostile[p.ID]:
			foes = append(foes, c)
		case colonist[p.ID]:
			friends = append(friends, c)
		}
	}
	for _, p := range view.Pawns {
		from, ok := p.Cell.Value()
		if !ok || !rocketLauncher(p.WeaponFacts) || !slices.Contains(view.Orderable, p.ID) || !live[p.ID] || m.Rescue.carrying(p.ID) {
			continue
		}
		reach := p.WeaponRange
		if reach <= 0 {
			reach = p.WeaponFacts.Range
		}
		i := slices.IndexFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == p.ID })
		if i >= 0 && m.Roles[i].Mortar != nil {
			continue
		}
		aim, found, most := domain.Cell{}, false, 0
		for _, c := range foes {
			if dist(from, c) > reach || slices.ContainsFunc(friends, func(f domain.Cell) bool { return dist(f, c) <= rocketSafeRadius }) {
				continue
			}
			n := 0
			for _, o := range foes {
				if dist(c, o) <= rocketClumpRadius {
					n++
				}
			}
			if n > most || n == most && found && dist(from, c) < dist(from, aim) {
				aim, found, most = c, true, n
			}
		}
		if most < rocketClumpMin {
			continue
		}
		if i < 0 {
			m.Roles = append(m.Roles, CombatRole{Pawn: p.ID, Ranged: true})
			i = len(m.Roles) - 1
		}
		m.Roles[i].Ground = &aim
	}
	m.Roles = sortRoles(m.Roles)
}
