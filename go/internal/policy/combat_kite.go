package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Kiting constants (#901, #1061).
const (
	// kiteSpeedRatio is how much faster than the fastest chaser a kiter
	// must be (the wiki's 120% Moving); kiteFastRatio against a pack with
	// an animal at least a colonist's speed (the wiki's 140%).
	kiteSpeedRatio = 1.2
	kiteFastRatio  = 1.4
	// kiteMinRange is the weapon range, in cells, of a kiter's long-range
	// gun: an assault rifle (30.9) or longer. A revolver or SMG must close
	// in and gets caught.
	kiteMinRange = 30.0
	// kiteMaxArmor is the heaviest worn sharp armor rating a kiter wears:
	// light armor, below a marine's. Unknown armor does not refuse.
	kiteMaxArmor = 0.8
	// kiteLeadRange: a hostile this close to the kiter, in cells, starts
	// the lead even before it targets the kiter.
	kiteLeadRange = 8
)

// DutyKiter baits a slow pack and leads it past the line (#901).
const DutyKiter CombatDuty = "kiter"

// kite is the one kiting step (#901, #923, #1061): a manhunter pack, and
// an unsupported mech raid under the hold or the sapper tactic. When
// kiteLead finds the chasers kitable, the fastest eligible gunner
// (kiterEligible) is the kiter. It baits the nearest hostile from where
// it stands; once a hostile targets it or comes within kiteLeadRange, it
// leads: it retreats to the lead cell, so the chaser crosses the firing
// squad's line (and the flank turrets), and engages again from there.
func kite(view CombatView, m *CombatMemory) {
	lure, need, ok := kiteLead(view, *m)
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
	armor := map[domain.PawnID]domain.Fact[float64]{}
	for _, d := range view.Defenders {
		armor[d.ID] = d.Armor
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
			if r.Duty == "" && kiterEligible(r, s, armor[r.Pawn], need) && s.MoveSpeed > best {
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

// kiterEligible is the wiki's kiter (#1061): a gunner with a long-range
// gun, no more than light armor, and a MoveSpeed of at least need.
func kiterEligible(r CombatRole, s CombatPawnState, armor domain.Fact[float64], need float64) bool {
	if a, known := armor.Value(); known && a > kiteMaxArmor {
		return false
	}
	return r.Ranged && s.WeaponRange >= kiteMinRange && s.MoveSpeed > 0 && s.MoveSpeed >= need
}

// kiteLead is the kiting step's lead cell and the MoveSpeed a kiter
// needs, ok false when the fight is not kited. A manhunter pack of known
// speeds is led to the inner-line cell farthest behind the firing line;
// the kiter needs kiteSpeedRatio of the fastest animal (an infestation's
// insects are lured out and kited alike, #1076), kiteFastRatio
// when any animal is at least a colonist's speed. A mech raid takes
// mechKiteLead.
func kiteLead(view CombatView, m CombatMemory) (domain.Cell, float64, bool) {
	if m.Tactic != TacticManhunter && m.Tactic != TacticInfestation {
		return mechKiteLead(view, m)
	}
	lure, ok := rearmostRetreat(view)
	if !ok {
		return domain.Cell{}, 0, false
	}
	fastest, ratio := 0.0, kiteSpeedRatio
	for _, h := range rankThreats(view) {
		if h.MoveSpeed <= 0 {
			return domain.Cell{}, 0, false
		}
		if !classifyAnimal(h).Slow {
			ratio = kiteFastRatio
		}
		fastest = max(fastest, h.MoveSpeed)
	}
	return lure, ratio * fastest, true
}

// mechKiteLead is kiteLead for a mech raid (#923): unsupported slow mechs
// (centipedes, breachers) are kited like a slow pack. Every live mech must
// be slower than a colonist, so a raid with fast support (scythers) is
// never kited. Under the hold the lead cell is the inner-line cell
// farthest behind the line; under the sapper tactic it is the predicted
// breach's first gunner post, inside the room.
func mechKiteLead(view CombatView, m CombatMemory) (domain.Cell, float64, bool) {
	if m.Tactic != TacticHold && m.Tactic != TacticSapper || !mechRaid(view) {
		return domain.Cell{}, 0, false
	}
	fastest := 0.0
	for _, h := range rankThreats(view) {
		if h.MoveSpeed <= 0 || h.MoveSpeed >= colonistMoveSpeed {
			return domain.Cell{}, 0, false
		}
		fastest = max(fastest, h.MoveSpeed)
	}
	need := kiteSpeedRatio * fastest
	if m.Tactic == TacticHold {
		lure, ok := rearmostRetreat(view)
		return lure, need, ok
	}
	b, ok := predictBreach(view)
	if !ok {
		return domain.Cell{}, 0, false
	}
	posts := breachPosts(b, sapperInset, sapperSpread, 1)
	if len(posts) == 0 {
		return domain.Cell{}, 0, false
	}
	return posts[0], need, true
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
