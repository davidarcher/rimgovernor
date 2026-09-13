package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestClassifyCaravanJourneyInFlight(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, true, true, nil); got != CaravanJourneyInFlight {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyStopped(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, true, false, nil); got != CaravanJourneyStopped {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyReturnedHomeRequiresEveryCrewMember(t *testing.T) {
	crew := []domain.PawnID{"pawn-1", "pawn-2"}
	roster := map[domain.PawnID]bool{"pawn-1": true, "pawn-2": true}
	if got := ClassifyCaravanJourney(crew, false, false, roster); got != CaravanJourneyReturnedHome {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyPartialRosterIsUnknown(t *testing.T) {
	crew := []domain.PawnID{"pawn-1", "pawn-2"}
	roster := map[domain.PawnID]bool{"pawn-1": true}
	if got := ClassifyCaravanJourney(crew, false, false, roster); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyAbsentEverywhereIsUnknownNotLost(t *testing.T) {
	crew := []domain.PawnID{"pawn-1"}
	if got := ClassifyCaravanJourney(crew, false, false, nil); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}

func TestClassifyCaravanJourneyEmptyCrewIsUnknown(t *testing.T) {
	if got := ClassifyCaravanJourney(nil, false, false, nil); got != CaravanJourneyUnknown {
		t.Fatal(got)
	}
}
