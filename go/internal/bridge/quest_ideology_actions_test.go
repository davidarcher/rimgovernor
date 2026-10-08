package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestHackIntentHasExplicitBooleanAndExactTarget(t *testing.T) {
	for _, enabled := range []bool{false, true} {
		h, _ := domain.NewHackDesignation("Terminal_1", enabled)
		a, _ := domain.NewHackDesignationAction("hack", h)
		wire, err := IntentAction("hack-key", a)
		if err != nil {
			t.Fatal(err)
		}
		intent := wire.GetHackDesignation()
		if intent == nil || intent.Enabled == nil || intent.GetEnabled() != enabled || intent.GetTarget().GetId() != "Terminal_1" {
			t.Fatal(intent)
		}
	}
}

func TestGiveIntentPreservesWholeRequestCountPrecondition(t *testing.T) {
	g, _ := domain.NewGiveItem("Pawn_1", "Pawn_2", "MedicineIndustrial", 9)
	a, _ := domain.NewGiveItemAction("gift", g)
	wire, err := IntentAction("gift-key", a)
	if err != nil {
		t.Fatal(err)
	}
	intent := wire.GetGiveItem()
	if intent == nil || intent.GetHauler().GetId() != "Pawn_1" || intent.GetRecipient().GetId() != "Pawn_2" || intent.GetDefinition() != "MedicineIndustrial" || intent.GetExpectedRemaining() != 9 {
		t.Fatal(intent)
	}
}
