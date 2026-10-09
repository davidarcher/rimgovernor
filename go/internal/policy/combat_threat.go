package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Threat tiers, most urgent first: the caster of a psychic ritual
// (ritualCaster), rocketeers and grenadiers, then
// anything in melee with a colonist, then sappers (a breaching termite
// among them), then the other mechs (inferno-cannon centipede > scyther >
// termite > the rest; a termite strips our cover), then everyone
// else. A go-juice raider does not go down from pain, so it ranks
// just after the sappers, ahead of the tribal tiers: gunners focus it to
// the kill instead of spreading shots that would down anyone else. Among
// tribals a berserker, fast and melee, ranks ahead of a pila
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
	}
	// The named tiers below rank the more urgent of the kind's and the
	// weapon's (the constants run most urgent first).
	named := threatOther
	if tier, ok := threatKinds[h.Kind]; ok {
		named = tier
	}
	if tier, ok := threatWeapons[h.Weapon]; ok {
		named = min(named, tier)
	}
	switch {
	case named != threatOther:
		return named
	case h.Mech:
		return threatMech
	}
	return threatOther
}

// threatKinds and threatWeapons are judgment tables: the game states no
// field that sets a tribal berserker, a pila thrower or a particular mech
// apart (a scyther and a termite carry no weapon def, and the pila's verb
// differs from a bow's only in numbers), so the tiers name the defs.
// The flamer needs no weapon entry: Gun_InfernoCannon's verb is explosive
// (WeaponFacts.Explosive), so it already ranks threatExplosive above.
var (
	threatKinds = map[string]int{
		"Tribal_Berserker":     threatBerserker,
		"Mech_CentipedeBurner": threatInfernoCentipede,
		"Mech_Scyther":         threatScyther,
		"Mech_Termite_Breach":  threatTermite,
	}
	threatWeapons = map[string]int{"Pila": threatPila}
)

// CaptureWorthy says a downed hostile is worth taking prisoner: a
// luciferium addict dies without a supply we would have to keep up,
// so the after-combat step strips or finishes it instead.
func CaptureWorthy(h CombatPawnState) bool { return !h.Luciferium }

// StripState is where the strip of a downed raider stands: not
// yet issued, its action still open, the designation placed, or the
// action refused or cancelled.
type StripState int

const (
	StripNone StripState = iota
	StripOpen
	StripPlaced
	StripRefused
)

// PostFightStep is the fight's next step for one downed raider:
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
// is not one a gunner can be ordered at.
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
	// An exploder near our pawns is never shot.
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
		// is in range.
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
