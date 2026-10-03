package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A choose-permit command builds one RoyaltyIntent (#1606).
func TestRoyaltyBuildsIntent(t *testing.T) {
	value, err := domain.NewRoyalty("pawn-7", "Empire", domain.RoyaltyChoosePermit, "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRoyaltyAction("roy1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("royalty is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetRoyalty()
	if got.GetPawnId() != "pawn-7" || got.GetFactionDef() != "Empire" || got.GetVerb() != "choose_permit" || got.GetPermit() != "CallMilitaryAidSmall" {
		t.Fatalf("%v", wire)
	}
}

func TestRoyaltyRefusesOtherKind(t *testing.T) {
	accept, err := domain.NewQuestAccept("quest-1", "", -1)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewQuestAcceptAction("q1", accept)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := royaltyAction(action); err == nil {
		t.Fatal("built a royalty write from a quest accept")
	}
}
