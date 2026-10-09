package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Psycasts in the fight: a psycaster that holds a known
// combat psycast casts it at the fight's hostiles or wounded defenders. A
// cast is a psycast_cast order, not a pawn job: the defense planner sends it
// as the generic Ability action, where native owns the guards
// (psyfocus, entropy, cooldown, range, target arm).
const (
	OrderCast  CombatOrderKind   = "psycast_cast"
	ReasonCast CombatOrderReason = "psycast"
)

// castFamily is what a psycast does in a fight; the royalty read carries no
// effect category, so psycastFamilies names the vanilla defs by family and
// every other def (utility, movement, an unknown mod def) is never cast.
type castFamily int

const (
	castNone castFamily = iota
	// castHeal mends a wounded defender; it outranks the rest.
	castHeal
	// castStun disables a hostile.
	castStun
	// castBurst damages hostiles.
	castBurst
	// castDefensive protects the caster.
	castDefensive
)

var psycastFamilies = map[string]castFamily{
	"Painblock":  castHeal,
	"Stun":       castStun,
	"Flashstorm": castBurst,
	"Firewall":   castBurst,
	"Skipshield": castDefensive,
}

const (
	// castReach is the most a caster stands from a target and still casts:
	// native refuses a target out of range, so Go sends none past it.
	castReach = 20.0
	// castHealBelow is the health fraction under which a defender is mended.
	castHealBelow = 0.6
)

// CastMark records a cast this fight: the stale royalty read has not seen it,
// so its cooldown, psyfocus cost and neural heat still count against the
// caster.
type CastMark struct {
	Pawn     domain.PawnID
	Def      string
	Tick     domain.Tick
	Psyfocus float64
	Entropy  float64
	Cooldown int
}

// castSpent sums the psyfocus and neural heat of a caster's casts this fight.
func castSpent(m *CombatMemory, pawn domain.PawnID) (focus, heat float64) {
	for _, c := range m.Casts {
		if c.Pawn == pawn {
			focus += c.Psyfocus
			heat += c.Entropy
		}
	}
	return focus, heat
}

func castCooling(view CombatView, m *CombatMemory, pawn domain.PawnID, def string) bool {
	return slices.ContainsFunc(m.Casts, func(c CastMark) bool {
		return c.Pawn == pawn && c.Def == def && int64(view.Tick)-int64(c.Tick) < int64(c.Cooldown)
	})
}

// castReady reports a known psycast that is affordable and off cooldown for a
// caster; every unread fact holds it.
func castReady(view CombatView, m *CombatMemory, state PsycasterState, pawn domain.PawnID, c Psycast) bool {
	cost, ck := c.PsyfocusCost.Value()
	gain, gk := c.Entropy.Value()
	_, kk := c.CooldownTicks.Value()
	left, lk := c.CooldownRemaining.Value()
	focus, fk := state.Psyfocus.Value()
	heat, hk := state.Entropy.Value()
	ceiling, mk := state.EntropyMax.Value()
	if !ck || !gk || !kk || !lk || !fk || !hk || !mk || left != 0 || castCooling(view, m, pawn, c.Def) {
		return false
	}
	spentFocus, spentHeat := castSpent(m, pawn)
	return focus-spentFocus >= cost && heat+spentHeat+gain <= ceiling
}

// castCalls are this stop's psycast casts: with a known royalty read and a
// live hostile, each standing colonist casts at most one ready combat
// psycast, the heal first, then stun, burst and defensive, in the order the
// read lists them. The target is the arm the ability takes (target kind), in
// reach of the caster; with none found the psycast waits.
func castCalls(view CombatView, m *CombatMemory) []CombatOrder {
	royalty, ok := view.Royalty.Value()
	if !ok || view.Hunt || len(liveHostiles(view)) == 0 {
		return nil
	}
	down := downPawns(view)
	casters := make([]PawnID, 0, len(royalty.Psycasts))
	for id := range royalty.Psycasts {
		casters = append(casters, id)
	}
	sort.Slice(casters, func(i, j int) bool { return casters[i] < casters[j] })
	var calls []CombatOrder
	for _, caster := range casters {
		pawn := domain.PawnID(caster)
		at, known := pawnCell(view, pawn)
		if down[pawn] || !known || !slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return d.ID == pawn }) {
			continue
		}
		var best CombatOrder
		var bestCast Psycast
		bestFamily := castNone
		for _, c := range royalty.Psycasts[caster] {
			family := psycastFamilies[c.Def]
			if family == castNone || bestFamily != castNone && family >= bestFamily || !castReady(view, m, royalty.Casters[caster], pawn, c) {
				continue
			}
			if order, found := castOrder(view, pawn, at, c, family); found {
				best, bestCast, bestFamily = order, c, family
			}
		}
		if bestFamily == castNone {
			continue
		}
		cost, _ := bestCast.PsyfocusCost.Value()
		gain, _ := bestCast.Entropy.Value()
		cool, _ := bestCast.CooldownTicks.Value()
		m.Casts = append(m.Casts, CastMark{Pawn: pawn, Def: bestCast.Def, Tick: view.Tick, Psyfocus: cost, Entropy: gain, Cooldown: cool})
		calls = append(calls, best)
	}
	return calls
}

// castOrder is the cast order of a psycast of a family at its target: the
// arm must match the ability's target kind (an unclassified kind holds).
func castOrder(view CombatView, caster domain.PawnID, at domain.Cell, c Psycast, family castFamily) (CombatOrder, bool) {
	order := CombatOrder{Pawn: caster, Kind: OrderCast, Permit: c.Def, Reason: ReasonCast, Arm: c.Target}
	hostile, hostileFound := castNearestHostile(view, at)
	switch family {
	case castHeal:
		wounded, cell, found := woundedDefender(view, at)
		if !found {
			return order, false
		}
		switch c.Target {
		case PsycastTargetPawn:
			order.Target = wounded
			return order, true
		case PsycastTargetSelf:
			return order, wounded == caster && cell == at
		}
	case castStun:
		if c.Target == PsycastTargetPawn && hostileFound {
			order.Target = hostile.ID
			return order, true
		}
	case castBurst:
		switch c.Target {
		case PsycastTargetPawn:
			if hostileFound {
				order.Target = hostile.ID
				return order, true
			}
		case PsycastTargetCell:
			if cell, found := strikeCell(view); found && dist(at, cell) <= castReach {
				order.Cell = cell
				return order, true
			}
		}
	case castDefensive:
		if !hostileFound {
			return order, false
		}
		switch c.Target {
		case PsycastTargetSelf:
			return order, true
		case PsycastTargetPawn:
			order.Target = caster
			return order, true
		}
	}
	return order, false
}

func pawnCell(view CombatView, id domain.PawnID) (domain.Cell, bool) {
	for _, p := range view.Pawns {
		if p.ID == id {
			return p.Cell.Value()
		}
	}
	return domain.Cell{}, false
}

// liveHostiles are the live hostile pawns with a known cell, in view order.
func liveHostiles(view CombatView) []CombatPawnState {
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = !t.Building && !positive(t.Dead) && !positive(t.Downed)
	}
	var out []CombatPawnState
	for _, p := range view.Pawns {
		if _, ok := p.Cell.Value(); ok && hostile[p.ID] && !p.Dead && !p.Downed {
			out = append(out, p)
		}
	}
	return out
}

// castNearestHostile is the live hostile nearest from within castReach.
func castNearestHostile(view CombatView, from domain.Cell) (CombatPawnState, bool) {
	var best CombatPawnState
	nearest := castReach
	found := false
	for _, p := range liveHostiles(view) {
		c, _ := p.Cell.Value()
		if d := dist(from, c); d <= nearest && (!found || d < nearest) {
			best, nearest, found = p, d, true
		}
	}
	return best, found
}

// woundedDefender is the defender with the lowest known health fraction
// under castHealBelow within castReach of from.
func woundedDefender(view CombatView, from domain.Cell) (domain.PawnID, domain.Cell, bool) {
	var id domain.PawnID
	var cell domain.Cell
	lowest := castHealBelow
	found := false
	for _, d := range view.Defenders {
		health, ok := d.HealthFraction.Value()
		c, cellKnown := pawnCell(view, d.ID)
		if !ok || !cellKnown || health >= lowest || dist(from, c) > castReach {
			continue
		}
		id, cell, lowest, found = d.ID, c, health, true
	}
	return id, cell, found
}
