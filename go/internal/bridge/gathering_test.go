package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A gathering builds one GatheringIntent with the def and organizer, no spot.
func TestGatheringBuildsIntent(t *testing.T) {
	value, err := domain.NewGathering("Party", "Human12")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewGatheringAction("g1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("gathering is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if g := wire.GetGathering(); g.GetGatheringDef() != "Party" || g.GetOrganizer().GetId() != "Human12" {
		t.Fatalf("%v", wire)
	}
}
