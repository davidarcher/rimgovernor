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

// insectKinds are the vanilla insectoid pawn kinds.
var insectKinds = []string{"Megascarab", "Spelopede", "Megaspider"}

func insectKind(kind string) bool { return slices.Contains(insectKinds, kind) }

// Infestation reports a fight with a live hive, or with at least one live
// hostile pawn and every live hostile pawn an insect.
func Infestation(view CombatView) bool {
	if slices.ContainsFunc(view.Structures, HostileStructure.hive) {
		return true
	}
	kinds := map[domain.PawnID]string{}
	for _, p := range view.Pawns {
		kinds[p.ID] = p.Kind
	}
	down := downPawns(view)
	n := 0
	for _, t := range view.Threats {
		id := domain.PawnID(t.ID)
		if t.Building || positive(t.Dead) || positive(t.Downed) || down[id] {
			continue
		}
		if !insectKind(kinds[id]) {
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
		if _, ok := grenadeBlast[carrier.Weapon]; !ok || isEMP(carrier.Weapon) {
			continue
		}
		from, ok := carrier.Cell.Value()
		if !ok || r.Cell != nil && from != *r.Cell {
			continue
		}
		reach := carrier.WeaponRange
		if reach <= 0 {
			reach = ProfileWeapon(EquipCandidateWeapon{Definition: carrier.Weapon}).Range
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
