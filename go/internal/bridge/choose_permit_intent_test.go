package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A choose_permit setting builds one PawnSettingsIntent arm.
func TestChoosePermitBuildsPawnSettingsIntent(t *testing.T) {
	value, err := domain.NewChoosePermitSetting("pawn-7", "Empire", "CallMilitaryAidSmall")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewPawnSettingsAction("roy1", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetPawnSettings()
	if got.GetPawnId() != "pawn-7" || got.GetChoosePermit().GetFactionDef() != "Empire" || got.GetChoosePermit().GetPermit() != "CallMilitaryAidSmall" {
		t.Fatalf("%v", wire)
	}
}

func TestChoosePermitRejectsColonInFaction(t *testing.T) {
	if _, err := domain.NewChoosePermitSetting("pawn-7", "Em:pire", "P"); err == nil {
		t.Fatal("a faction def with a colon would not survive the store")
	}
}
