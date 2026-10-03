package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticInfestation is an infestation's own formation (#1071): a fight with
// a live hive, or whose live hostile pawns are all insects, picks it.
// Every fighter commits at once, brawlers block the tunnel or door with
// relief (#864), grenadiers throw at the hive (#1049), and no mortar is
// crewed: hives only spawn under overhead mountain, which shells cannot
// reach.
const TacticInfestation CombatTactic = "infestation"

// Infestation reports a fight with a live hive, or with at least one live
// hostile pawn and every live hostile pawn an insect.
func Infestation(view CombatView) bool {
	if slices.ContainsFunc(view.Structures, HostileStructure.hive) {
		return true
	}
	insects := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		insects[p.ID] = p.Insect
	}
	down := downPawns(view)
	n := 0
	for _, t := range view.Threats {
		id := domain.PawnID(t.ID)
		if t.Building || positive(t.Dead) || positive(t.Downed) || down[id] {
			continue
		}
		if !insects[id] {
			return false
		}
		n++
	}
	return n > 0
}

// infestationFormation commits every armed fighter at once: gunners on the
// line, brawlers blocking the choke with a relief reserve. These are the
// manhunter formation's roles (insects hold no exploder), less the peeler
// waiting at home (#865): it charges the top insect, never a trickle.
func infestationFormation(view CombatView, geometry GeometryReply, relieved []domain.PawnID) []CombatRole {
	roles := manhunterFormation(view, geometry, relieved)
	// The top insect, else (no insect out) the hive itself.
	var top domain.PawnID
	if ranked := rankThreats(view); len(ranked) > 0 {
		top = ranked[0].ID
	} else if i := slices.IndexFunc(view.Structures, HostileStructure.hive); i >= 0 {
		top = view.Structures[i].ID
	}
	for i := range roles {
		switch {
		case roles[i].Duty == DutyPeeler:
			roles[i] = CombatRole{Pawn: roles[i].Pawn, Target: top}
		case roles[i].Duty == "" && roles[i].Target == "":
			roles[i].Target = top
		}
	}
	return roles
}

// hiveGrenade aims each infestation role holding a frag or molotov primary
// at the nearest live hive in its range and clear of colonists (#1049's
// scatter rule); with none it falls back to GrenadeTarget on the insects.
// Every mortar role is dropped: the hive is under overhead mountain.
func hiveGrenade(view CombatView, m *CombatMemory) {
	if m.Tactic != TacticInfestation {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var colonists []domain.Cell
	for _, d := range view.Defenders {
		if s, ok := state[d.ID]; ok && !s.Dead {
			if c, known := s.Cell.Value(); known {
				colonists = append(colonists, c)
			}
		}
	}
	hostiles := rankThreats(view)
	for i := range m.Roles {
		r := &m.Roles[i]
		r.Mortar, r.Aim = nil, nil
		if m.Rescue.carrying(r.Pawn) {
			continue
		}
		carrier := state[r.Pawn]
		if carrier.WeaponFacts.Burner() {
			// Molotovs belong to the burn-out alone (#1122): flame on an
			// insect may send the hive to assault.
			r.Ground = nil
			continue
		}
		if carrier.WeaponFacts.Blast <= 0 || carrier.WeaponFacts.EMP {
			continue
		}
		from, ok := carrier.Cell.Value()
		if !ok || r.Cell != nil && from != *r.Cell {
			continue
		}
		reach := carrier.WeaponRange
		if reach <= 0 {
			reach = carrier.WeaponFacts.Range
		}
		var best *domain.Cell
		for _, s := range view.Structures {
			c := s.Cell
			if !s.hive() || dist(from, c) > reach || nearColonist(c, colonists) {
				continue
			}
			if best == nil || dist(from, c) < dist(from, *best) || dist(from, c) == dist(from, *best) && (c.X < best.X || c.X == best.X && c.Z < best.Z) {
				best = &c
			}
		}
		if best == nil {
			if c, ok := GrenadeTarget(carrier, hostiles, colonists); ok {
				best = &c
			}
		}
		r.Ground = best
	}
}

// heatEntry holds every melee fighter of an infestation fight in place
// while the hive reads above heatEntryMaxC, instead of sending it at an
// insect or the hive; a downed insect, the burn-out's finish, is still
// fair game. An unknown temperature changes nothing.
func heatEntry(view CombatView, m *CombatMemory) {
	temp, known := view.HiveTemperatureC.Value()
	if m.Tactic != TacticInfestation || !known || temp <= heatEntryMaxC {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	down := downPawns(view)
	for i := range m.Roles {
		r := &m.Roles[i]
		if !r.Ranged && r.Cell == nil && r.Target != "" && !down[r.Target] {
			r.Target = ""
			if c, ok := state[r.Pawn].Cell.Value(); ok {
				r.Cell = &c
			}
		}
	}
}

// deepDrillJob is a colonist's job while it works a deep drill.
const deepDrillJob = "OperateDeepDrill"

// drillEvacuate is the deep-drill spawn (#1076): insects tunnel up near a
// working drill, with no hive. The colonist working the drill when the
// fight starts is latched as the driller and evacuates: it drops its
// target and duty and retreats to the rearmost inner-line cell, else
// kiteLeadRange cells straight away from the nearest insect, while the
// formation's brawlers melee-block. Later stops keep the latch although
// the driller's job has changed.
func drillEvacuate(view CombatView, m *CombatMemory, orderable map[domain.PawnID]bool) {
	if m.Tactic != TacticInfestation || slices.ContainsFunc(view.Structures, HostileStructure.hive) {
		m.Driller = ""
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	if m.Driller == "" {
		for _, d := range view.Defenders {
			if s := state[d.ID]; s.Job == deepDrillJob && !s.Dead && !s.Downed {
				m.Driller = d.ID
				break
			}
		}
	}
	s := state[m.Driller]
	if m.Driller == "" || s.Dead || s.Downed {
		return
	}
	to, ok := rearmostRetreat(view)
	if !ok {
		at, known := s.Cell.Value()
		var near *domain.Cell
		for _, h := range rankThreats(view) {
			if c, ok := h.Cell.Value(); ok && known && (near == nil || distance2(c, at) < distance2(*near, at)) {
				near = &c
			}
		}
		if near == nil {
			return
		}
		to = domain.Cell{X: at.X + kiteLeadRange*sign(at.X-near.X), Z: at.Z + kiteLeadRange*sign(at.Z-near.Z)}
	}
	if m.Kiter == m.Driller {
		m.Kiter, m.Leading = "", false
	}
	i := slices.IndexFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == m.Driller })
	if i < 0 {
		if !orderable[m.Driller] {
			return
		}
		m.Roles = append(m.Roles, CombatRole{})
		i = len(m.Roles) - 1
	}
	m.Roles[i] = CombatRole{Pawn: m.Driller, Cell: &to, Retreat: true}
}
