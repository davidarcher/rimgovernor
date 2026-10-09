package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A ritual command builds one RitualIntent.
func TestRitualBuildsIntent(t *testing.T) {
	value, err := domain.NewRitual("pawn-7", domain.RitualBestowing, domain.RitualStart)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRitualAction("ritual1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("ritual is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetRitual()
	if got.GetPawnId() != "pawn-7" || got.GetRitual() != "bestowing" || got.GetVerb() != "start" {
		t.Fatalf("%v", wire)
	}
}

func TestRitualRefusesOtherKind(t *testing.T) {
	accept, err := domain.NewQuestAccept("quest-1", "", -1)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewQuestAcceptAction("q1", accept)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := ritualAction(action); err == nil {
		t.Fatal("built a ritual from a quest accept")
	}
}
