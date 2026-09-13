package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// CaravanJourneyStatus classifies where a previously-departed crew stands,
// using only evidence Go already has a validated read for: whether the
// caravan is still a live world object (bridge.ReadWorldProgression), whether
// its crew has reappeared on the home map (bridge.ReadHomeColonists), and
// whether its crew is visible on some other live map in that same
// world-progression census (bridge.WorldProgressionRead.Maps). RimGovernor
// never claims custody of a foreign map's pawns or items -- seeing a crew
// member there only rules out "lost", it never authorizes reading or
// writing anything further about that map. A crew member found in none of
// these censuses cannot be presumed lost, home or elsewhere -- only Unknown.
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
	// CaravanJourneyOnForeignMap means the caravan is no longer a live
	// world object, not every crew member is confirmed home, but at least
	// one crew member is visible in the current world-progression census
	// on some non-home map (WorldMap.Home false) -- most likely an
	// ambush/encounter map RimWorld opened when the caravan was attacked.
	// This still commits to nothing about that map's custody or the rest
	// of the crew's fate; it only distinguishes "we can see them, alive,
	// somewhere" from true Unknown, which is the difference between a
	// caravan a human should go look at and one that may simply still be
	// travelling.
	CaravanJourneyOnForeignMap
)

// ClassifyCaravanJourney decides a tracked caravan's status from one
// WorldProgression caravan-census lookup, the home-colonist roster it last
// departed from, and every other map's pawn roster from the same
// world-progression read. found reports whether that census still lists
// the caravan (bridge.WorldProgressionRead.Caravans, matched by ID); when
// found is true, moving carries its native pather.Moving fact. crew is the
// departure's original crew; homeRoster is the set of pawn IDs
// bridge.ReadHomeColonists most recently reported alive on the home map;
// foreignRoster is the set of pawn IDs bridge.WorldProgressionRead.Maps
// reports on any map with Home false in that same read. A pawn found in
// neither homeRoster nor foreignRoster nor still a live caravan cannot be
// presumed lost -- only Unknown.
func ClassifyCaravanJourney(crew []domain.PawnID, found bool, moving bool, homeRoster map[domain.PawnID]bool, foreignRoster map[domain.PawnID]bool) CaravanJourneyStatus {
	if found {
		if moving {
			return CaravanJourneyInFlight
		}
		return CaravanJourneyStopped
	}
	if len(crew) == 0 {
		return CaravanJourneyUnknown
	}
	home := true
	foreign := false
	for _, pawn := range crew {
		if !homeRoster[pawn] {
			home = false
		}
		if foreignRoster[pawn] {
			foreign = true
		}
	}
	if home {
		return CaravanJourneyReturnedHome
	}
	if foreign {
		return CaravanJourneyOnForeignMap
	}
	return CaravanJourneyUnknown
}
