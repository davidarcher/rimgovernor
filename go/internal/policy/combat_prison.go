package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticPrisonBreak answers a prison break (#1080): the escapees are
// subdued, not killed. Armored wardens body-block them, one brawler per
// injured escapee and two unarmed wardens per healthy one; ranged wardens
// with low-DPS weapons hold fire until an escapee is blocked, then shoot it.
const TacticPrisonBreak CombatTactic = "prison_break"

// ReasonPrisonFire is a ranged warden's fire-mode toggle: hold until the
// escapee is blocked, then fire at will.
const ReasonPrisonFire CombatOrderReason = "prison_fire"

// prisonLethal are ranged weapons too deadly to shoot an escapee with:
// snipers, heavy and area weapons.
var prisonLethal = map[string]bool{
	"Gun_SniperRifle": true, "Gun_BoltActionRifle": true, "Gun_ChargeLance": true, "Gun_Minigun": true,
	"Gun_LMG": true, "Gun_ChargeRifle": true, "Gun_AssaultRifle": true, "Gun_HeavySMG": true,
}

// prisonSafeRanged is a low-DPS ranged weapon: no sniper, heavy, rocket,
// launcher or grenade.
func prisonSafeRanged(def string, facts WeaponDef) bool {
	return facts.Ranged && !prisonLethal[def] && !facts.Explosive
}

// escapees are the live prison-breaking prisoners in id order.
func escapees(view CombatView) []CombatPawnState {
	var out []CombatPawnState
	for _, p := range view.Pawns {
		if p.Prisoner && !p.Dead && !p.Downed {
			out = append(out, p)
		}
	}
	return out
}

// injured is an escapee below full summary health.
func injured(p CombatPawnState) bool {
	h, ok := p.Health.Value()
	return ok && h < 1
}

// prisonBreakTurn runs the prison-break tactic when the view has a live
// escapee: it reports false otherwise and leaves memory alone. The
// formation is recomputed every stop (it is deterministic, so a pawn
// already doing its role gets nothing), and the orders are the roles'
// plus the ranged wardens' fire modes.
func prisonBreakTurn(view CombatView, next *CombatMemory, state map[domain.PawnID]CombatPawnState, orderable map[domain.PawnID]bool) ([]CombatOrder, bool) {
	out := escapees(view)
	if len(out) == 0 {
		return nil, false
	}
	if next.Tactic != TacticPrisonBreak {
		next.Formed = view.Tick
	}
	next.Tactic, next.Refusal = TacticPrisonBreak, ""
	next.Roles = prisonFormation(view, out)
	blocked := prisonBlocked(next.Roles, out, state)
	lastHold := map[domain.PawnID]bool{}
	for _, o := range next.Issued {
		lastHold[o.Pawn] = o.Kind == OrderFireMode && o.FireMode == HoldFire
	}
	var orders []CombatOrder
	for i, role := range next.Roles {
		s := state[role.Pawn]
		if !orderable[role.Pawn] || s.Dead || s.Downed {
			continue
		}
		if role.Ranged {
			held := s.FireMode == HoldFire || s.FireMode == "" && lastHold[role.Pawn]
			target := firstBlocked(out, blocked)
			next.Roles[i].Target = target
			switch {
			case target == "" && !held:
				orders = append(orders, CombatOrder{Pawn: role.Pawn, Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonPrisonFire})
				continue
			case target == "":
				continue
			case held:
				orders = append(orders, CombatOrder{Pawn: role.Pawn, Kind: OrderFireMode, FireMode: FireAtWill, Reason: ReasonPrisonFire})
			}
			role = next.Roles[i]
		}
		want, ok := role.want(s)
		if !ok || next.doing(want, s) || interruptsAim(s) {
			continue
		}
		orders = append(orders, want)
	}
	return orders, true
}

// firstBlocked is the first blocked escapee, "" when none is.
func firstBlocked(out []CombatPawnState, blocked map[domain.PawnID]bool) domain.PawnID {
	for _, e := range out {
		if blocked[e.ID] {
			return e.ID
		}
	}
	return ""
}

// prisonBlocked are the escapees a blocker holds: a blocker assigned to
// it stands next to it or is in melee with it.
func prisonBlocked(roles []CombatRole, out []CombatPawnState, state map[domain.PawnID]CombatPawnState) map[domain.PawnID]bool {
	cells := map[domain.PawnID]domain.Cell{}
	for _, e := range out {
		if c, ok := e.Cell.Value(); ok {
			cells[e.ID] = c
		}
	}
	blocked := map[domain.PawnID]bool{}
	for _, r := range roles {
		if r.Ranged || r.Target == "" {
			continue
		}
		s := state[r.Pawn]
		at, ok := s.Cell.Value()
		escapee, known := cells[r.Target]
		if s.Target == r.Target && s.Stance == StanceMelee || ok && known && adjacent8(at, escapee) {
			blocked[r.Target] = true
		}
	}
	return blocked
}

// prisonFormation assigns the blockers, injured escapees first, then the
// ranged wardens. Blockers are eligible defenders without a gun: for an
// injured escapee the best brawler (blunt weapon, then unarmed, then any
// melee weapon), for a healthy one two unarmed wardens (unarmed first,
// then blunt), each rank wardens first, then best armored. Every eligible
// defender left with a low-DPS gun is a ranged warden; the rest stay out.
func prisonFormation(view CombatView, out []CombatPawnState) []CombatRole {
	weapon := map[domain.PawnID]string{}
	facts := map[domain.PawnID]WeaponDef{}
	for _, p := range view.Pawns {
		weapon[p.ID], facts[p.ID] = p.Weapon, p.WeaponFacts
	}
	var melee, ranged []SquadDefenderFacts
	for _, d := range byArmor(slices.Clone(view.Defenders)) {
		switch {
		case !squadDefenderEligible(d):
		case positive(d.RangedEquipped) || facts[d.ID].Ranged:
			if prisonSafeRanged(weapon[d.ID], facts[d.ID]) {
				ranged = append(ranged, d)
			}
		default:
			melee = append(melee, d)
		}
	}
	grip := func(d SquadDefenderFacts) int {
		switch {
		case facts[d.ID].Blunt:
			return 0
		case weapon[d.ID] == "" && !positive(d.Armed):
			return 1
		}
		return 2
	}
	rank := func(pool []SquadDefenderFacts, order func(SquadDefenderFacts) int) {
		sort.SliceStable(pool, func(i, j int) bool {
			if pool[i].Warden != pool[j].Warden {
				return pool[i].Warden
			}
			return order(pool[i]) < order(pool[j])
		})
	}
	ordered := slices.Clone(out)
	sort.SliceStable(ordered, func(i, j int) bool { return injured(ordered[i]) && !injured(ordered[j]) })
	used := map[domain.PawnID]bool{}
	var roles []CombatRole
	for _, e := range ordered {
		need, order := 2, func(d SquadDefenderFacts) int { return [3]int{1, 0, 2}[grip(d)] }
		if injured(e) {
			need, order = 1, grip
		}
		pool := slices.DeleteFunc(slices.Clone(melee), func(d SquadDefenderFacts) bool { return used[d.ID] })
		rank(pool, order)
		for _, d := range pool[:min(need, len(pool))] {
			used[d.ID] = true
			roles = append(roles, CombatRole{Pawn: d.ID, Target: e.ID, Duty: DutyBlocker})
		}
	}
	for _, d := range ranged {
		roles = append(roles, CombatRole{Pawn: d.ID, Ranged: true})
	}
	return sortRoles(roles)
}
