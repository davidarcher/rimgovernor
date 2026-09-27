package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Threat tiers (#863), most urgent first: rocketeers and grenadiers, then
// anything in melee with a colonist, then sappers and termites, then the
// other mechs (inferno-cannon centipede > scyther > the rest), then
// everyone else.
const (
	threatExplosive = iota
	threatMeleeColonist
	threatSapper
	threatInfernoCentipede
	threatScyther
	threatMech
	threatOther
)

// explosiveWeapons are def-name fragments of the launchers and grenades
// that make a raider a rocketeer or grenadier.
var explosiveWeapons = []string{"Rocket", "Grenade", "Molotov", "Launcher", "Doomsday"}

// threatTier ranks one live hostile; colonists are our defenders.
func threatTier(h CombatPawnState, colonists map[domain.PawnID]bool) int {
	for _, frag := range explosiveWeapons {
		if strings.Contains(h.Weapon, frag) {
			return threatExplosive
		}
	}
	if h.Stance == StanceMelee && colonists[h.Target] {
		return threatMeleeColonist
	}
	switch {
	case h.Sapper || strings.Contains(h.Kind, "Termite"):
		return threatSapper
	case strings.Contains(h.Kind, "CentipedeBurner") || h.Weapon == "Gun_InfernoCannon":
		return threatInfernoCentipede
	case strings.Contains(h.Kind, "Scyther"):
		return threatScyther
	case strings.HasPrefix(h.Kind, "Mech_"):
		return threatMech
	}
	return threatOther
}

// rankThreats is the live hostile pawns (not buildings) by threat score,
// ties by id: the order gunners focus fire in.
func rankThreats(view CombatView) []CombatPawnState {
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	colonists := map[domain.PawnID]bool{}
	for _, d := range view.Defenders {
		colonists[d.ID] = true
	}
	var out []CombatPawnState
	tier := map[domain.PawnID]int{}
	for _, t := range view.Threats {
		id := domain.PawnID(t.ID)
		s, seen := state[id]
		if !seen {
			s = CombatPawnState{ID: id}
		}
		if t.Building || positive(t.Dead) || positive(t.Downed) || s.Dead || s.Downed {
			continue
		}
		tier[id] = threatTier(s, colonists)
		out = append(out, s)
	}
	sort.SliceStable(out, func(i, j int) bool {
		if tier[out[i].ID] != tier[out[j].ID] {
			return tier[out[i].ID] < tier[out[j].ID]
		}
		return out[i].ID < out[j].ID
	})
	return out
}

// inRange says a shooter at from with weapon range reach can reach h;
// an unknown cell or range counts as in range.
func inRange(from domain.Fact[domain.Cell], reach float64, h CombatPawnState) bool {
	a, ok := from.Value()
	b, ok2 := h.Cell.Value()
	if !ok || !ok2 || reach <= 0 {
		return true
	}
	return math.Hypot(float64(a.X-b.X), float64(a.Z-b.Z)) <= reach
}

// focusFire points every gunner (a ranged role) at the top-scored hostile
// in its range, keeping the target it had before this stop (prior) while
// that one lives and stays in range, so the focus is stable across stops.
// A gunner with no hostile in range keeps the formation's target.
func focusFire(view CombatView, roles, prior []CombatRole) []CombatRole {
	ranked := rankThreats(view)
	live := map[domain.PawnID]CombatPawnState{}
	for _, h := range ranked {
		live[h.ID] = h
	}
	was := map[domain.PawnID]domain.PawnID{}
	for _, r := range prior {
		was[r.Pawn] = r.Target
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	out := slices.Clone(roles)
	for i, r := range out {
		if !r.Ranged {
			continue
		}
		s := state[r.Pawn]
		from := s.Cell
		if r.Cell != nil {
			from = domain.Known(*r.Cell)
		}
		if h, ok := live[was[r.Pawn]]; ok && inRange(from, s.WeaponRange, h) {
			out[i].Target = h.ID
			continue
		}
		for _, h := range ranked {
			if inRange(from, s.WeaponRange, h) {
				out[i].Target = h.ID
				break
			}
		}
	}
	return out
}
