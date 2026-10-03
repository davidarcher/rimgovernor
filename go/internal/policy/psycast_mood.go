package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Mood psycasts (#1612, epic #1598): a colonist whose mood pressure is active
// and whose measured need relief is spent (or absent) is cast on by a
// colonist who knows a mood psycast. The mood relief planner owns the call;
// it is the generic Ability action (#1610), where native owns the cooldown,
// psyfocus, neural heat and range guards. Go holds the cast on every unread
// fact and keeps a psyfocus reserve back for the casters' other work.
//
// Only casts with a clear, safe payoff for an unattended colony are listed
// here; docs/developers/contracts/controller-contracts.md names the vanilla
// psycasts left undone. Matching is case-insensitive on the AbilityDef name.
var moodCasts = []string{"WordOfJoy"}

// PsycastPsyfocusReserve is the Psyfocus (0-1) a caster keeps after a mood
// cast, so a colonist never spends itself dry on a mood and keeps what its
// other psycasts need.
const PsycastPsyfocusReserve = 0.25

// PsycastCall is one caster casting one psycast on one pawn.
type PsycastCall struct {
	Caster  PawnID
	Ability string
	Target  PawnID
}

// MoodCastExhausted reports a caster-ability pair whose bounded attempts for
// the target are spent.
type MoodCastExhausted func(caster PawnID, ability string) bool

func isMoodCast(def string) bool {
	return slices.ContainsFunc(moodCasts, func(c string) bool { return strings.EqualFold(c, def) })
}

// SelectMoodCast is the mood cast for target, or false. A caster must be a
// different, available colonist with a read psyfocus at least the cast's cost
// plus PsycastPsyfocusReserve, know a pawn-targeted mood psycast whose cost,
// neural heat and cooldown the read carries, and not be exhausted for the
// target. The caster with the most psyfocus wins, then the lowest id. The
// pawn's current neural heat and the cast's remaining cooldown are not read:
// native refuses a cast that would overflow either, and the bounded attempts
// count that refusal.
func SelectMoodCast(target PawnID, royalty domain.Fact[RoyaltyFacts], pawns domain.Fact[[]WorkPawn], exhausted MoodCastExhausted) (PsycastCall, bool) {
	facts, rk := royalty.Value()
	rows, pk := pawns.Value()
	if !rk || !pk {
		return PsycastCall{}, false
	}
	var best PsycastCall
	bestFocus := -1.0
	for _, pawn := range rows {
		if pawn.ID == target {
			continue
		}
		available, ak := pawn.Available.Value()
		focus, fk := pawn.Psyfocus.Value()
		if !ak || !available || !fk {
			continue
		}
		for _, cast := range facts.Psycasts[pawn.ID] {
			cost, ck := cast.PsyfocusCost.Value()
			_, ek := cast.Entropy.Value()
			_, dk := cast.CooldownTicks.Value()
			if !isMoodCast(cast.Def) || cast.Target != PsycastTargetPawn || !ck || !ek || !dk ||
				focus < cost+PsycastPsyfocusReserve || exhausted != nil && exhausted(pawn.ID, cast.Def) {
				continue
			}
			if focus > bestFocus || focus == bestFocus && pawn.ID < best.Caster {
				best, bestFocus = PsycastCall{Caster: pawn.ID, Ability: cast.Def, Target: target}, focus
			}
			break
		}
	}
	return best, bestFocus >= 0
}
