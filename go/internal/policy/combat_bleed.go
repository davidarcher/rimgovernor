package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bleedDownSeverity is the BloodLoss severity whose consciousness loss
// downs a pawn (#1035); death is at 1.0.
const bleedDownSeverity = 0.6

// Contained reports a hostile the fight can leave alone (#1035): it is
// fleeing or its raid is leaving, or no colonist is inside its weapon
// range, or every other hostile is downed, dead or fleeing. A hostile
// targeting a colonist is never contained: colonist safety wins.
func Contained(view CombatView, id domain.PawnID) bool {
	colonists := map[domain.PawnID]bool{}
	for _, d := range view.Defenders {
		colonists[d.ID] = true
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	s := state[id]
	if colonists[s.Target] {
		return false
	}
	fleeing := func(h domain.PawnID) bool {
		for _, t := range view.Positional {
			if domain.PawnID(t.ID) == h {
				toil, ok := t.LordToilClass.Value()
				return ok && podStrikeToils[toil]
			}
		}
		return false
	}
	if fleeing(id) {
		return true
	}
	for _, t := range view.Positional {
		if domain.PawnID(t.ID) != id {
			continue
		}
		if d, ok := t.NearestColonistDistance.Value(); ok && s.WeaponRange > 0 && d > s.WeaponRange {
			return true
		}
	}
	for _, t := range view.Threats {
		h := domain.PawnID(t.ID)
		if h == id || t.Building {
			continue
		}
		if !positive(t.Dead) && !positive(t.Downed) && !state[h].Dead && !state[h].Downed && !fleeing(h) {
			return false
		}
	}
	return true
}

// willBleedDown reports a hostile whose blood loss crosses the downing
// point before it kills: the game's death-on-downed roll does not run on
// a blood-loss downing, so it survives to be captured (#1035). Unknown
// health is false.
func willBleedDown(s CombatPawnState) bool {
	loss, lk := s.BloodLoss.Value()
	rate, rk := s.BleedRatePerDay.Value()
	death, dk := s.HoursUntilBleedDeath.Value()
	if !lk || !rk || !dk || rate <= 0 || loss >= 1 {
		return false
	}
	return max(0, (bleedDownSeverity-loss)/rate*24) < death
}

// sparedBleeders are the hostiles the fight holds fire on (#1035): below
// the population target, a live humanlike that is contained and will
// bleed down.
func sparedBleeders(view CombatView) map[domain.PawnID]bool {
	out := map[domain.PawnID]bool{}
	if pop, ok := view.Population.Value(); !ok || pop >= domain.PopulationTarget {
		return out
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for _, t := range view.Threats {
		id := domain.PawnID(t.ID)
		s, seen := state[id]
		if !seen || t.Building || !positive(t.Humanlike) || positive(t.Dead) || positive(t.Downed) || s.Dead || s.Downed {
			continue
		}
		if willBleedDown(s) && Contained(view, id) {
			out[id] = true
		}
	}
	return out
}

// spare drops the spared hostiles from the view's threats, so no tactic
// assigns or retargets an attacker onto them.
func (v CombatView) spare(spared map[domain.PawnID]bool) CombatView {
	if len(spared) == 0 {
		return v
	}
	v.Threats = slices.DeleteFunc(slices.Clone(v.Threats), func(t SquadThreatFacts) bool { return spared[domain.PawnID(t.ID)] })
	v.Positional = slices.DeleteFunc(slices.Clone(v.Positional), func(t DefensiveThreatFacts) bool { return spared[domain.PawnID(t.ID)] })
	return v
}

// onlySpared reports that every live threat left is spared: gunners then
// hold fire rather than pick the spared hostile on fire-at-will.
func onlySpared(view CombatView, spared map[domain.PawnID]bool) bool {
	if len(spared) == 0 {
		return false
	}
	down := downPawns(view)
	for _, t := range view.Threats {
		if !positive(t.Dead) && !positive(t.Downed) && !down[domain.PawnID(t.ID)] {
			return false
		}
	}
	return true
}
