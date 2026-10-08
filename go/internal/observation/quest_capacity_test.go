package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestFrameQuestColonistsAtHome(t *testing.T) {
	emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "home", Dead: domain.Known(false)}, {ID: "away", Dead: domain.Known(false)}}}
	read := &bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 1, Home: true, PawnIDs: []string{"home", "away", "animal"}}}, Caravans: []bridge.CaravanJourney{{PawnIDs: []string{"away"}}}}
	n, known := frameQuestColonistsAtHome(read, 1, emergency).Value()
	if !known || n != 1 {
		t.Fatalf("home=%d,%v", n, known)
	}
	if _, known := frameQuestColonistsAtHome(nil, 1, emergency).Value(); known {
		t.Fatal("missing world census was known")
	}
	emergency.ColonistsComplete = domain.Unknown[bool]()
	if _, known := frameQuestColonistsAtHome(read, 1, emergency).Value(); known {
		t.Fatal("missing colonist roster was known")
	}
}
