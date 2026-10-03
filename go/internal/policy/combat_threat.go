package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Threat tiers (#863), most urgent first: the caster of a psychic ritual
// (#1739, ritualCaster), rocketeers and grenadiers, then
// anything in melee with a colonist, then sappers (a breaching termite
// among them), then the other mechs (inferno-cannon centipede > scyther >
// termite > the rest; a termite strips our cover, #927), then everyone
// else. A go-juice raider (#1056) does not go down from pain, so it ranks
// just after the sappers, ahead of the tribal tiers: gunners focus it to
// the kill instead of spreading shots that would down anyone else. Among
// tribals (#1055) a berserker, fast and melee, ranks ahead of a pila
// thrower, whose volley hits hard at short range; both come after sappers
// and ahead of the mechs they never raid with.
const (
	threatRitualCaster = iota
	threatExplosive
	threatMeleeColonist
	threatSapper
	threatGoJuice
	threatBerserker
	threatPila
	threatInfernoCentipede
	threatScyther
	threatTermite
	threatMech
	threatOther
)

// threatTier ranks one live hostile; colonists are our defenders. A raider
// with an explosive weapon (a launcher or a grenade, by its def rows) is a
// rocketeer or grenadier.
func threatTier(h CombatPawnState, colonists map[domain.PawnID]bool) int {
	if h.WeaponFacts.Explosive {
		return threatExplosive
	}
	if h.Stance == StanceMelee && colonists[h.Target] {
		return threatMeleeColonist
	}
	switch {
	case h.Sapper:
		return threatSapper
	case h.GoJuice:
		return threatGoJuice
	case h.Kind == "Tribal_Berserker":
		return threatBerserker
	case h.Weapon == "Pila":
		return threatPila
	case strings.Contains(h.Kind, "CentipedeBurner") || h.Weapon == "Gun_InfernoCannon":
		return threatInfernoCentipede
	case strings.Contains(h.Kind, "Scyther"):
		return threatScyther
	case strings.Contains(h.Kind, "Termite"):
		return threatTermite
	case strings.HasPrefix(h.Kind, "Mech_"):
		return threatMech
	}
	return threatOther
}

// CaptureWorthy says a downed hostile is worth taking prisoner: a
// luciferium addict dies without a supply we would have to keep up
// (#1056), so the after-combat step strips or finishes it instead (#1079).
func CaptureWorthy(h CombatPawnState) bool { return !h.Luciferium }

// StripState is where the strip of a downed raider stands (#1079): not
// yet issued, its action still open, the designation placed, or the
// action refused or cancelled.
type StripState int

const (
	StripNone StripState = iota
	StripOpen
	StripPlaced
	StripRefused
)

// PostFightStep is the fight's next step for one downed raider (#1079):
// strip it, wait for the strip, or it is done (stripped). A raider not
// CaptureWorthy is then finished; a worthy one is left to capture.
type PostFightStep string

const (
	PostFightStrip PostFightStep = "strip"
	PostFightWait  PostFightStep = "wait"
	PostFightDone  PostFightStep = "done"
)

// PostFightNext strips a downed raider still wearing apparel (or of
// unknown apparel) and waits until its apparel is gone. A refused strip,
// or a placed one whose apparel the frame no longer shows, is done rather
// than holding the fight on a missing row.
func PostFightNext(worn domain.Fact[bool], strip StripState) PostFightStep {
	w, known := worn.Value()
	switch {
	case strip == StripOpen:
		return PostFightWait
	case known && !w:
		return PostFightDone
	case strip == StripNone:
		return PostFightStrip
	case strip == StripPlaced && known && w:
		return PostFightWait
	}
	return PostFightDone
}

// rankThreats is the live hostile pawns (not buildings) by threat score,
// ties by id: the order gunners focus fire in. A pawn hidden from the player
// is not one a gunner can be ordered at (#1739).
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
		if t.Building || positive(t.Dead) || positive(t.Downed) || s.Dead || s.Downed || hiddenFromPlayer(t) {
			continue
		}
		tier[id] = threatTier(s, colonists)
		if ritualCaster(t) {
			tier[id] = threatRitualCaster
		}
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
	// An exploder near our pawns is never shot (#898).
	near := nearExploders(view)
	ranked := slices.DeleteFunc(rankThreats(view), func(h CombatPawnState) bool { return near[h.ID] })
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
	locked := meleeLocked(view, roles)
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
		// A hostile locked with our blocker is passed over while another
		// is in range (#978).
		pick := func(h CombatPawnState) bool { return inRange(from, s.WeaponRange, h) && !locked[h.ID] }
		if !slices.ContainsFunc(ranked, pick) {
			pick = func(h CombatPawnState) bool { return inRange(from, s.WeaponRange, h) }
		}
		if h, ok := live[was[r.Pawn]]; ok && pick(h) {
			out[i].Target = h.ID
			continue
		}
		if j := slices.IndexFunc(ranked, pick); j >= 0 {
			out[i].Target = ranked[j].ID
		}
		if near[out[i].Target] {
			out[i].Target = ""
		}
	}
	return out
}
