package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// CaravanJourneyStatus classifies where a previously-departed crew stands,
// using only evidence Go already has a validated read for: whether the
// caravan is still a live world object (bridge.ReadWorldProgression), and
// whether its crew has reappeared on the home map (bridge.ReadHomeColonists).
// RimGovernor never reads a foreign map's pawns, so a crew member found in
// neither census cannot be presumed lost, home or elsewhere -- only Unknown.
// This mirrors the domain.Fact discipline used throughout the codebase: an
// absence of evidence is not evidence of absence.
type CaravanJourneyStatus int

const (
	// CaravanJourneyUnknown means the evidence available does not
	// distinguish "still travelling", "arrived elsewhere" or "returned
	// home". Nothing about colony custody may be reconciled from it.
	CaravanJourneyUnknown CaravanJourneyStatus = iota
	// CaravanJourneyInFlight means the caravan is still a live world
	// object and native reports it moving.
	CaravanJourneyInFlight
	// CaravanJourneyStopped means the caravan is still a live world
	// object but has stopped (arrived at a non-home tile, resting, or
	// paused before entering a map). Its crew and cargo remain away;
	// nothing may be reconciled into home colony custody yet.
	CaravanJourneyStopped
	// CaravanJourneyReturnedHome means the caravan is no longer a live
	// world object (it entered a map) and every expected crew member is
	// now confirmed alive on the home map. This is the only status a
	// caller may use to reconcile crew/cargo back into home custody.
	CaravanJourneyReturnedHome
)

// ClassifyCaravanJourney decides a tracked caravan's status from one
// WorldProgression caravan-census lookup and the home-colonist roster it
// last departed from. found reports whether that census still lists the
// caravan (bridge.WorldProgressionRead.Caravans, matched by ID); when found
// is true, moving carries its native pather.Moving fact. crew is the
// departure's original crew; homeRoster is the set of pawn IDs
// bridge.ReadHomeColonists most recently reported alive on the home map.
func ClassifyCaravanJourney(crew []domain.PawnID, found bool, moving bool, homeRoster map[domain.PawnID]bool) CaravanJourneyStatus {
	if found {
		if moving {
			return CaravanJourneyInFlight
		}
		return CaravanJourneyStopped
	}
	if len(crew) == 0 {
		return CaravanJourneyUnknown
	}
	for _, pawn := range crew {
		if !homeRoster[pawn] {
			return CaravanJourneyUnknown
		}
	}
	return CaravanJourneyReturnedHome
}
