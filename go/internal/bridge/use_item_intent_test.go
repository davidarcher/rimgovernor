package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestUseItemIntentWire(t *testing.T) {
	use, err := domain.NewUseItem("Human1", "Apparel_PsychicShockLance7", "Human2")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewUseItemAction("lance-0", use)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("use_item is not an intent-mode kind")
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetUseItem()
	if wire.GetKey() != "key-1" || got.GetPawnId() != "Human1" || got.GetItemId() != "Apparel_PsychicShockLance7" || got.GetTargetId() != "Human2" {
		t.Fatalf("wire = %v", wire)
	}
}
