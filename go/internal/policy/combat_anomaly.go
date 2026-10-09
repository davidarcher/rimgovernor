package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Anomaly threats in defense tactics. Every fact here is a native
// def or object read on the pawn row (PawnAnomaly); unknown is never one of
// the cases below, so a pawn the native read could not classify keeps the
// tactic it had before.

// anomalous reports a hostile that is an Anomaly entity or a mutant (a
// shambler is a humanlike mutant, so Humanlike alone cannot place it).
func anomalous(t SquadThreatFacts) bool {
	a, ok := t.Anomaly.Value()
	return ok && (positive(a.Entity) || positive(a.Mutant))
}

// MeleeEntity reports an entity or mutant whose attack is melee with no
// offensive ability. It charges in, so a choke blocks it and a slow one is
// kited, as a manhunter pack is (manhunterFormation); one with a ranged or
// offensive-ability attack shoots over the block and is not.
func MeleeEntity(t SquadThreatFacts) bool {
	a, ok := t.Anomaly.Value()
	return ok && (positive(a.Entity) || positive(a.Mutant)) && positive(a.MeleeOnly)
}

// ritualCaster reports the pawn casting its lord's psychic ritual. A cultist
// psychic ritual siege only needs its caster to finish the ritual, and
// damage or interruption to the caster makes every cultist attack directly,
// so the caster is the one to hurt: https://rimworldwiki.com/wiki/Psychic_ritual_siege
func ritualCaster(t SquadThreatFacts) bool {
	a, ok := t.Anomaly.Value()
	return ok && positive(a.PsychicRitualInvoker)
}

// hiddenFromPlayer reports a pawn the player cannot see or target (an
// unrevealed sightstealer): an attack order naming it is refused, so it is
// never a target until it shows itself.
func hiddenFromPlayer(t SquadThreatFacts) bool {
	a, ok := t.Anomaly.Value()
	return ok && positive(a.HiddenFromPlayer)
}

// ritualCasterCells are the live ritual casters' known cells, not within
// MortarSafeRadius of a colonist: shelled while they cast, before a
// colonist is near enough for the scatter to land on it.
func ritualCasterCells(view CombatView) []domain.Cell {
	caster := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		caster[domain.PawnID(t.ID)] = !t.Building && ritualCaster(t) && !positive(t.Dead) && !positive(t.Downed)
	}
	ours := colonistCells(view)
	var out []domain.Cell
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok && caster[p.ID] && !p.Dead && !p.Downed && !nearAny(ours, c) {
			out = append(out, c)
		}
	}
	return out
}
