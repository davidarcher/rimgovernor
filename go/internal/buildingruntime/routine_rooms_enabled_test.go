package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A serve composing only recovery and sheltering must still read the room
// census: without it SafeAreaOwed stays unknown, no Safe area is drawn and
// sheltering never moves a pawn (CI run 36957589404, #1560).
func TestRoomsEnabledForMaintainShelter(t *testing.T) {
	r := &RoutineReviewer{methods: domain.Known([]policy.ConcernID{policy.RecoverDisasterServices, policy.MaintainShelter})}
	if !r.roomsEnabled() {
		t.Fatal("MaintainShelter composed without a rooms read")
	}
	if (&RoutineReviewer{methods: domain.Known([]policy.ConcernID{policy.RecoverDisasterServices})}).roomsEnabled() {
		t.Fatal("recovery alone reads rooms")
	}
}
