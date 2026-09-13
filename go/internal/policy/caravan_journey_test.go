package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestClassifyCaravanJourneyInFlight(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, true, true, nil, nil); got != CaravanJourneyInFlight {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyStopped(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, true, false, nil, nil); got != CaravanJourneyStopped {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyReturnedHomeRequiresEveryCrewMember(t *testing.T) {
	crew := []domain.PawnID{"pawn-1", "pawn-2"}
	roster := map[domain.PawnID]bool{"pawn-1": true, "pawn-2": true}
	if got := ClassifyCaravanJourney(crew, false, false, roster, nil); got != CaravanJourneyReturnedHome {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyPartialRosterIsUnknownWithoutForeignSighting(t *testing.T) {
	crew := []domain.PawnID{"pawn-1", "pawn-2"}
	roster := map[domain.PawnID]bool{"pawn-1": true}
	if got := ClassifyCaravanJourney(crew, false, false, roster, nil); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyAbsentEverywhereIsUnknownNotLost(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, false, false, nil, nil); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyEmptyCrewIsUnknown(t *testing.T) {
	if got := ClassifyCaravanJourney(nil, false, false, nil, nil); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyForeignSightingIsOnForeignMap(t *testing.T) {
	crew := []domain.PawnID{"pawn-1", "pawn-2"}
	// pawn-1 is visible on a non-home map; pawn-2 is accounted for nowhere.
	// The caravan is not lost -- at least one crew member is confirmed
	// alive somewhere -- but this is not a home return: nothing here may be
	// reconciled into home custody.
	foreign := map[domain.PawnID]bool{"pawn-1": true}
	if got := ClassifyCaravanJourney(crew, false, false, nil, foreign); got != CaravanJourneyOnForeignMap {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyForeignSightingNeverOverridesReturnedHome(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	home := map[domain.PawnID]bool{"pawn-1": true}
	foreign := map[domain.PawnID]bool{"pawn-1": true}
	if got := ClassifyCaravanJourney(crew, false, false, home, foreign); got != CaravanJourneyReturnedHome {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyStillFoundIgnoresForeignRoster(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	foreign := map[domain.PawnID]bool{"pawn-1": true}
	if got := ClassifyCaravanJourney(crew, true, true, nil, foreign); got != CaravanJourneyInFlight {
		t.Fatal(got)
	}
}
