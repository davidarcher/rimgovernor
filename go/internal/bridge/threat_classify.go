package bridge

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ThreatProximityRadius is the Chebyshev radius around a colonist within
// which an unowned downed pawn or predator is a nearby threat. It is the
// native status read's default predator radius, which the snapshot frame's
// census uses; the native filter drops such pawns further out to save the
// row, and the rule here holds on its own for the rows the native keeps for
// other facts.
const ThreatProximityRadius = 30.0

// ClassifyThreat is the threat rule over one native threat fact row
// (native emits facts, Go decides):
//   - a pawn in a Manhunter mental state, of a faction hostile to the
//     player, or breaking out of prison is Hostile; hostility
//     precedes every other rule;
//   - else a PredatorHunt is an IgnoredHunter when the predator is ours or
//     its prey resolves to a pawn that is not ours, and a HuntingPredator
//     otherwise (prey ours, or prey unreadable);
//   - else an unowned pawn within ThreatProximityRadius of a colonist is
//     NearbyDowned when downed and NearbyPredator when a predator (a downed
//     predator is both).
//
// Any other row (a non-manhunter mental state, say) classifies as nothing.
func ClassifyThreat(row *o.ThreatPawn) []policy.ThreatKind {
	if threatManhunter(row.GetMentalState()) || row.GetFactionHostile() || row.GetPrisonBreak() {
		return []policy.ThreatKind{policy.Hostile}
	}
	if row.GetPredatorHunt() {
		if row.GetOurs() || row.Prey != nil && !row.GetPreyIsOurs() {
			return []policy.ThreatKind{policy.IgnoredHunter}
		}
		return []policy.ThreatKind{policy.HuntingPredator}
	}
	if row.GetOurs() || row.NearestColonistDistance == nil || row.GetNearestColonistDistance() > ThreatProximityRadius {
		return nil
	}
	var kinds []policy.ThreatKind
	if row.GetDowned() {
		kinds = append(kinds, policy.NearbyDowned)
	}
	if row.GetPredator() {
		kinds = append(kinds, policy.NearbyPredator)
	}
	return kinds
}

// threatManhunter matches any Manhunter mental state def name, as the
// native classifier did (Manhunter, ManhunterPermanent, modded variants).
func threatManhunter(mental string) bool {
	return strings.Contains(strings.ToLower(mental), "manhunter")
}
